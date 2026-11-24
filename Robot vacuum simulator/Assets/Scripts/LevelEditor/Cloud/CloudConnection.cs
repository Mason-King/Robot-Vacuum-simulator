using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using UnityEngine;

namespace RobotVacuum.LevelEditor.Cloud
{
    /// <summary>
    /// The cloud connection the Settings page edits (5.5, 4.8, C.12): which cloud account, region, resource name prefix,
    /// network and compute limits batch campaigns use. "Bring your own cloud": campaigns run in the user's own AWS
    /// account, set up by the templates in cloud/infra, and the user pays AWS directly.
    ///
    /// The same JSON file is read by the vacuumctl command-line tool (cloud/vacuumctl/connection.py), so both always
    /// agree. It holds NO secrets (C.12): only the name of an AWS profile or the path of a credentials file. The keys
    /// stay where AWS keeps them (~/.aws, the AWS SSO cache, or a credential_process helper). Nothing here is kept in
    /// PlayerPrefs, and nothing is sent anywhere until the user presses a button on the Settings page (1.1.1).
    ///
    /// Field names, defaults and validation rules match connection.py; cloud/tests/test_connection.py checks that.
    /// </summary>
    [Serializable]
    public sealed class CloudConnection
    {
        public const int CurrentSchemaVersion = 1;
        public const string FileName = "cloud-connection.json";
        public const string AppFolder = "RobotVacuumSim";
        public const string DefaultPrefix = "robot-vacuum-sim";

        // Identical to cloud/vacuumctl/connection.py.
        public const string RegionPattern = @"^[a-z]{2}(-gov)?-[a-z]+-\d$";
        public const string PrefixPattern = @"^[a-z][a-z0-9-]{1,30}[a-z0-9]$";
        public const string VpcIdPattern = @"^vpc-[0-9a-f]{8,17}$";
        public const string SubnetIdPattern = @"^subnet-[0-9a-f]{8,17}$";
        public const string ProxyPattern = @"^https?://[^\s/:]+(:\d{1,5})?/?$";
        public const string InstanceTypesPattern = @"^[a-z0-9]+(\.[a-z0-9]+)?(,[a-z0-9]+(\.[a-z0-9]+)?)*$";
        public const int MinVpcPrefix = 16;
        public const int MaxVpcPrefix = 20;
        public const int MaxVCpusLimit = 4096;
        public const int MinNodeTimeoutSeconds = 60;
        public const int MaxNodeTimeoutSeconds = 1209600;

        public static readonly string[] SignInMethods = { "profile", "sso", "credentialsFile" };
        public static readonly string[] NetworkModes = { "newVpc", "existingVpc" };

        public int schemaVersion = CurrentSchemaVersion;
        public string provider = "aws";
        public AwsSettings aws = new AwsSettings();
        public NetworkSettings network = new NetworkSettings();
        public ComputeSettings compute = new ComputeSettings();
        public ToolSettings tool = new ToolSettings();

        [Serializable]
        public sealed class AwsSettings
        {
            /// <summary>profile, sso or credentialsFile. Every method ends in a named AWS profile.</summary>
            public string signIn = "profile";
            public string profile = "default";

            /// <summary>credentialsFile only: the file's path. Its contents are never copied.</summary>
            public string credentialsFile = "";
            public string region = "us-east-2";

            /// <summary>Every AWS resource name starts with this, so several teams can share one account.</summary>
            public string resourcePrefix = DefaultPrefix;
        }

        [Serializable]
        public sealed class NetworkSettings
        {
            /// <summary>newVpc: an isolated VPC per campaign, deleted with it. existingVpc: the organisation's own.</summary>
            public string mode = "newVpc";
            public string vpcCidr = "10.42.0.0/16";

            /// <summary>newVpc only. False = private subnets and VPC endpoints, no internet gateway (4.8).</summary>
            public bool nodeInternetAccess = true;
            public string existingVpcId = "";
            public List<string> existingSubnetIds = new List<string>();

            /// <summary>This computer's proxy for its calls to AWS (corporate networks). No user name or password.</summary>
            public string httpsProxy = "";
        }

        [Serializable]
        public sealed class ComputeSettings
        {
            /// <summary>SPOT (cheaper, can be interrupted; interrupted nodes resume) or EC2 (on-demand).</summary>
            public string provisioning = "SPOT";
            public int maxVCpus = 32;
            public string instanceTypes = "";
            public int nodeTimeoutSeconds = 21600;
        }

        /// <summary>How the simulator starts vacuumctl. Empty = found automatically. vacuumctl ignores this section.</summary>
        [Serializable]
        public sealed class ToolSettings
        {
            public string python = "";
            public string cloudFolder = "";
        }

        public bool IsExistingVpc => network.mode == "existingVpc";

        // ---------------------------------------------------------------- file

        /// <summary>
        /// %APPDATA%\RobotVacuumSim\cloud-connection.json on Windows (~/.config/... elsewhere), the same place vacuumctl
        /// looks. VACUUM_CLOUD_CONNECTION overrides it, as for vacuumctl.
        /// </summary>
        public static string DefaultPath
        {
            get
            {
                string overridden = Environment.GetEnvironmentVariable("VACUUM_CLOUD_CONNECTION");
                if (!string.IsNullOrEmpty(overridden)) return overridden;
                string config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(config))
                    config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                return Path.Combine(config, AppFolder, FileName);
            }
        }

        /// <summary>
        /// Reads the file. A missing file gives the defaults. An unreadable one also gives the defaults, with
        /// <paramref name="problem"/> saying why, so the caller can tell the user before it is replaced (C.10).
        /// </summary>
        public static CloudConnection Load(string path, out bool exists, out string problem)
        {
            problem = null;
            exists = File.Exists(path);
            if (!exists) return new CloudConnection();

            try
            {
                var loaded = JsonUtility.FromJson<CloudConnection>(File.ReadAllText(path));
                if (loaded == null) throw new FormatException("the file is empty");
                loaded.Normalise();
                return loaded;
            }
            catch (Exception exception)
            {
                problem = $"The cloud settings file couldn't be read ({exception.Message}). Changing a setting replaces it.";
                return new CloudConnection();
            }
        }

        /// <summary>Written to a temporary file and moved into place, so vacuumctl never reads half a file.</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(this, true));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        public string ToJson() => JsonUtility.ToJson(this, true);

        void Normalise()
        {
            aws ??= new AwsSettings();
            network ??= new NetworkSettings();
            compute ??= new ComputeSettings();
            tool ??= new ToolSettings();
            network.existingSubnetIds ??= new List<string>();
            if (string.IsNullOrEmpty(provider)) provider = "aws";
        }

        // ---------------------------------------------------------------- validation (C.2: plain sentences)

        /// <summary>Every problem with these settings. Empty = usable. Same rules as vacuumctl's connection.validate.</summary>
        public List<string> Validate(bool checkFiles = true)
        {
            Normalise();
            var errors = new List<string>();
            if (schemaVersion != CurrentSchemaVersion)
                errors.Add($"schemaVersion {schemaVersion} isn't supported (expected {CurrentSchemaVersion}).");
            if (!CloudProviders.IsAvailable(provider))
                errors.Add($"Provider \"{provider}\" isn't available. Available: AWS. Planned: {CloudProviders.PlannedLabels}.");

            if (Array.IndexOf(SignInMethods, aws.signIn) < 0)
                errors.Add($"Sign-in method \"{aws.signIn}\" must be one of: AWS profile, AWS SSO, credentials file.");
            if (string.IsNullOrWhiteSpace(aws.profile) || Regex.IsMatch(aws.profile, @"\s"))
                errors.Add("AWS profile name: enter the profile's name (no spaces), e.g. default or vacuum.");
            if (aws.signIn == "credentialsFile")
            {
                if (string.IsNullOrEmpty(aws.credentialsFile))
                    errors.Add("Credentials file: choose the file that holds the AWS keys.");
                else if (checkFiles && !File.Exists(ExpandHome(aws.credentialsFile)))
                    errors.Add($"Credentials file: {aws.credentialsFile} doesn't exist.");
            }
            if (!Matches(aws.region, RegionPattern))
                errors.Add($"Region \"{aws.region}\" doesn't look like an AWS region, e.g. us-east-2.");
            if (!Matches(aws.resourcePrefix, PrefixPattern))
                errors.Add($"Resource name prefix \"{aws.resourcePrefix}\": use 3-32 lowercase letters, digits or hyphens, " +
                           "starting with a letter and not ending with a hyphen (e.g. acme-vacuum).");

            if (Array.IndexOf(NetworkModes, network.mode) < 0)
                errors.Add($"Network mode \"{network.mode}\" must be \"newVpc\" or \"existingVpc\".");
            if (network.mode == "newVpc")
            {
                string cidrProblem = CheckVpcCidr(network.vpcCidr);
                if (cidrProblem != null) errors.Add(cidrProblem);
            }
            else if (network.mode == "existingVpc")
            {
                if (!Matches(network.existingVpcId, VpcIdPattern))
                    errors.Add($"Existing VPC ID \"{network.existingVpcId}\" should look like vpc-0abc1234.");
                if (network.existingSubnetIds.Count == 0)
                    errors.Add("Existing subnets: list at least one subnet ID (two in different zones is better).");
                else
                {
                    var bad = network.existingSubnetIds.FindAll(s => !Matches(s, SubnetIdPattern));
                    if (bad.Count > 0) errors.Add($"Existing subnets: {string.Join(", ", bad)} don't look like subnet-0abc1234.");
                    if (new HashSet<string>(network.existingSubnetIds).Count != network.existingSubnetIds.Count)
                        errors.Add("Existing subnets: a subnet is listed twice.");
                }
            }
            if (!string.IsNullOrEmpty(network.httpsProxy) && !Matches(network.httpsProxy, ProxyPattern))
                errors.Add($"HTTPS proxy \"{network.httpsProxy}\" should look like http://proxy.example.com:8080 (no user name or password).");

            if (compute.provisioning != "SPOT" && compute.provisioning != "EC2")
                errors.Add("Provisioning must be \"SPOT\" or \"EC2\" (on-demand).");
            if (compute.maxVCpus < 1 || compute.maxVCpus > MaxVCpusLimit)
                errors.Add($"Maximum vCPUs must be a whole number from 1 to {MaxVCpusLimit}.");
            if (compute.nodeTimeoutSeconds < MinNodeTimeoutSeconds || compute.nodeTimeoutSeconds > MaxNodeTimeoutSeconds)
                errors.Add("Node time limit must be 60 seconds to 14 days.");
            string types = (compute.instanceTypes ?? string.Empty).Replace(" ", string.Empty);
            if (types.Length > 0 && !Matches(types, InstanceTypesPattern))
                errors.Add($"Instance types \"{compute.instanceTypes}\": comma-separated, e.g. c6i.large,c7i.large.");
            return errors;
        }

        /// <summary>Null when <paramref name="cidr"/> is a usable new-VPC range: /16-/20, host bits clear, RFC 1918.</summary>
        public static string CheckVpcCidr(string cidr)
        {
            string[] parts = (cidr ?? string.Empty).Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                parts[0].Split('.').Length != 4 || !int.TryParse(parts[1], out int prefix) || prefix < 0 || prefix > 32)
                return $"VPC address range \"{cidr}\" isn't a network address, e.g. 10.42.0.0/16.";

            byte[] b = address.GetAddressBytes();
            uint value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            if ((value & ~mask) != 0) return $"VPC address range \"{cidr}\" isn't a network address, e.g. 10.42.0.0/16.";
            if (prefix < MinVpcPrefix || prefix > MaxVpcPrefix)
                return $"VPC address range {cidr}: use a /{MinVpcPrefix} to /{MaxVpcPrefix} range.";

            bool isPrivate = (b[0] == 10) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
            return isPrivate ? null : $"VPC address range {cidr}: use a private range (10.x, 172.16-31.x or 192.168.x).";
        }

        /// <summary>A comma- or space-separated list of subnet IDs, cleaned up.</summary>
        public static List<string> ParseList(string text)
        {
            var list = new List<string>();
            foreach (string part in (text ?? string.Empty).Split(new[] { ',', ' ', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                list.Add(part.Trim());
            return list;
        }

        /// <summary>One line describing the network, as vacuumctl prints it.</summary>
        public string DescribeNetwork() => IsExistingVpc
            ? $"existing VPC {network.existingVpcId} ({network.existingSubnetIds.Count} subnet(s))"
            : $"new VPC {network.vpcCidr} per campaign, " + (network.nodeInternetAccess ? "with internet access" : "private, VPC endpoints only");

        static bool Matches(string value, string pattern) => value != null && Regex.IsMatch(value, pattern);

        static string ExpandHome(string path) => path.StartsWith("~")
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(1).TrimStart('/', '\\'))
            : path;
    }

    /// <summary>
    /// Where campaigns can run (the backend interface, 5.5). AWS ships now; the others are planned (C.4). Adding one
    /// means a backend class in cloud/vacuumctl/backends.py plus its own settings section on the Settings page.
    /// </summary>
    public static class CloudProviders
    {
        public struct Provider
        {
            public string id;
            public string label;
            public bool available;
        }

        public static readonly Provider[] All =
        {
            new Provider { id = "aws", label = "AWS", available = true },
            new Provider { id = "azure", label = "Microsoft Azure", available = false },
            new Provider { id = "gcp", label = "Google Cloud", available = false },
            new Provider { id = "ssh", label = "SSH / on-premises cluster", available = false },
        };

        public static bool IsAvailable(string id) => Array.Exists(All, p => p.id == id && p.available);

        public static string PlannedLabels => string.Join(", ", Array.ConvertAll(Array.FindAll(All, p => !p.available), p => p.label));
    }
}
