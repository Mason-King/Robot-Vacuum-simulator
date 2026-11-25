using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.LevelEditor;
using RobotVacuum.LevelEditor.Cloud;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// The Settings page's cloud connection (5.5, 4.8, C.12). cloud/tests/test_connection.py checks the same rules
    /// and file format from vacuumctl's side.
    /// </summary>
    public class CloudConnectionTests
    {
        static CloudConnection Valid() => new CloudConnection();

        [Test]
        public void DefaultsAreValid()
        {
            CollectionAssert.IsEmpty(Valid().Validate(checkFiles: false));
        }

        [Test]
        public void NetworkRanges()
        {
            Assert.IsNull(CloudConnection.CheckVpcCidr("10.42.0.0/16"));
            Assert.IsNull(CloudConnection.CheckVpcCidr("172.20.0.0/20"));
            Assert.IsNull(CloudConnection.CheckVpcCidr("192.168.0.0/18"));
            Assert.IsNotNull(CloudConnection.CheckVpcCidr("10.0.0.0/8"), "too large");
            Assert.IsNotNull(CloudConnection.CheckVpcCidr("10.0.0.0/24"), "too small for two /24 subnets");
            Assert.IsNotNull(CloudConnection.CheckVpcCidr("8.8.0.0/16"), "public range");
            Assert.IsNotNull(CloudConnection.CheckVpcCidr("10.0.0.1/16"), "host bits set");
            Assert.IsNotNull(CloudConnection.CheckVpcCidr("10.0/16"), "not four parts");
            Assert.IsNotNull(CloudConnection.CheckVpcCidr("nonsense"));
        }

        [Test]
        public void ExistingVpcNeedsIds()
        {
            var c = Valid();
            c.network.mode = "existingVpc";
            Assert.IsTrue(c.Validate(false).Any(e => e.StartsWith("Existing VPC ID")));
            Assert.IsTrue(c.Validate(false).Any(e => e.StartsWith("Existing subnets")));

            c.network.existingVpcId = "vpc-0abc12345";
            c.network.existingSubnetIds = CloudConnection.ParseList("subnet-0abc12345, subnet-0def67890");
            CollectionAssert.IsEmpty(c.Validate(false));

            c.network.existingSubnetIds.Add("subnet-0abc12345");
            Assert.IsTrue(c.Validate(false).Any(e => e.Contains("listed twice")));
        }

        [Test]
        public void AccountRules()
        {
            var c = Valid();
            c.aws.region = "Ohio";
            c.aws.resourcePrefix = "Acme_";
            c.aws.profile = "my profile";
            c.aws.signIn = "credentialsFile";
            var errors = c.Validate(false);
            Assert.AreEqual(4, errors.Count, string.Join("\n", errors));
        }

        [Test]
        public void ProxyMayNotCarryCredentials()
        {
            var c = Valid();
            c.network.httpsProxy = "http://user:secret@proxy:8080";
            Assert.IsTrue(c.Validate(false).Any(e => e.StartsWith("HTTPS proxy")));
            c.network.httpsProxy = "http://proxy.corp:8080";
            CollectionAssert.IsEmpty(c.Validate(false));
        }

        [Test]
        public void OnlyAvailableProvidersPass()
        {
            var c = Valid();
            c.provider = "azure";
            Assert.IsTrue(c.Validate(false).Any(e => e.Contains("Planned")));
        }

        [Test]
        public void SaveAndLoadRoundTripWithoutSecrets()
        {
            string folder = Path.Combine(Path.GetTempPath(), "cloud-connection-test-" + System.Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, CloudConnection.FileName);
            try
            {
                var c = Valid();
                c.aws.profile = "vacuum";
                c.network.nodeInternetAccess = false;
                c.network.existingSubnetIds = new List<string> { "subnet-0abc12345" };
                c.Save(path);
                c.aws.region = "eu-west-1";
                c.Save(path); // replaces the existing file

                var loaded = CloudConnection.Load(path, out bool exists, out string problem);
                Assert.IsTrue(exists);
                Assert.IsNull(problem);
                Assert.AreEqual("vacuum", loaded.aws.profile);
                Assert.AreEqual("eu-west-1", loaded.aws.region);
                Assert.IsFalse(loaded.network.nodeInternetAccess);
                Assert.IsFalse(File.Exists(path + ".tmp"));

                string text = File.ReadAllText(path).ToLowerInvariant();
                foreach (string secret in new[] { "secret", "password", "accesskey", "sessiontoken" })
                    StringAssert.DoesNotContain(secret, text);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [Test]
        public void ReadsTheFileVacuumctlWrites()
        {
            // As cloud/vacuumctl/connection.py saves it (json.dump, indent=2).
            const string fromPython = @"{
  ""schemaVersion"": 1, ""provider"": ""aws"",
  ""aws"": { ""signIn"": ""sso"", ""profile"": ""acme"", ""credentialsFile"": """", ""region"": ""us-west-2"", ""resourcePrefix"": ""acme-vacuum"" },
  ""network"": { ""mode"": ""existingVpc"", ""vpcCidr"": ""10.42.0.0/16"", ""nodeInternetAccess"": true,
                 ""existingVpcId"": ""vpc-0abc12345"", ""existingSubnetIds"": [""subnet-0abc12345"", ""subnet-0def67890""], ""httpsProxy"": """" },
  ""compute"": { ""provisioning"": ""EC2"", ""maxVCpus"": 64, ""instanceTypes"": ""c6i.large"", ""nodeTimeoutSeconds"": 7200 },
  ""tool"": { ""python"": """", ""cloudFolder"": """" }
}";
            var c = JsonUtility.FromJson<CloudConnection>(fromPython);
            CollectionAssert.IsEmpty(c.Validate(false));
            Assert.AreEqual("sso", c.aws.signIn);
            Assert.AreEqual(2, c.network.existingSubnetIds.Count);
            Assert.AreEqual(7200, c.compute.nodeTimeoutSeconds);
            StringAssert.StartsWith("existing VPC vpc-0abc12345", c.DescribeNetwork());
        }

        [Test]
        public void BrokenFileIsReportedNotThrown()
        {
            string path = Path.Combine(Path.GetTempPath(), "broken-connection-" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{ not json");
                var c = CloudConnection.Load(path, out bool exists, out string problem);
                Assert.IsTrue(exists);
                Assert.IsNotNull(problem);
                CollectionAssert.IsEmpty(c.Validate(false), "falls back to the defaults");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void ArgumentsAreQuotedForTheCommandLine()
        {
            string joined = VacuumctlProcess.JoinArguments(new[] { "admin-policy", "--out", @"C:\My Files\policy.json", "say \"hi\"", "" });
            Assert.AreEqual(@"admin-policy --out ""C:\My Files\policy.json"" ""say \""hi\"""" """"", joined);
            Assert.AreEqual(@"""C:\dir with space\\""", VacuumctlProcess.JoinArguments(new[] { @"C:\dir with space\" }));
        }

        [Test]
        public void TestReportParsesVacuumctlJson()
        {
            var report = SettingsView.TestReport.Parse(@"{ ""ok"": false, ""provider"": ""aws"",
                ""checks"": [ { ""name"": ""Sign-in"", ""status"": ""fail"", ""message"": ""expired"" } ] }");
            Assert.IsNotNull(report);
            Assert.IsFalse(report.ok);
            Assert.AreEqual("fail", report.checks[0].status);
            Assert.IsNull(SettingsView.TestReport.Parse("not json"));
            Assert.IsNull(SettingsView.TestReport.Parse(""));
        }
    }
}
