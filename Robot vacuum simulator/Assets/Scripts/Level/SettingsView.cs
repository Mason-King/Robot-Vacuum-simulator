using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor.Cloud;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The start screen's Settings page. Its Cloud connection section is "bring your own cloud" (5.5): the user connects
    /// their own organisation's AWS account, picks the network campaigns run in, sets up the resources there from the
    /// templates, and can remove them all again. It edits <see cref="CloudConnection"/> (saved as it changes) and runs
    /// the vacuumctl tool for anything that talks to AWS, so the simulator itself never holds a credential (C.12) and
    /// never touches the network unless a button here is pressed (1.1.1).
    /// </summary>
    public sealed class SettingsView : VisualElement
    {
        const int MaxOutputLines = 300;

        // A command keeps running if the user leaves the page; coming back shows it again.
        static VacuumctlProcess running;
        static readonly List<string> output = new List<string>();
        static TestReport lastReport;

        readonly OverlayLayer overlay;
        readonly ScrollView content;
        readonly string path;
        readonly CloudConnection connection;
        readonly List<VisualElement> actionButtons = new List<VisualElement>();

        Label problemsLabel;
        Label runningLabel;
        VisualElement cancelButton;
        VisualElement checksBox;
        VisualElement outputBox;
        IVisualElementScheduledItem pump;

        public event Action BackRequested;

        public SettingsView(OverlayLayer overlay)
        {
            this.overlay = overlay;
            AddToClassList("le-screen");
            AddToClassList("le-settings");

            Ui.PageHeader(this, "Settings", "Start screen", () => BackRequested?.Invoke(), out var subtitle);
            subtitle.text = "Cloud connection for batch campaigns";

            content = new ScrollView(ScrollViewMode.Vertical);
            content.AddToClassList("le-page__content");
            content.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            Add(content);

            path = CloudConnection.DefaultPath;
            connection = CloudConnection.Load(path, out _, out string problem);
            Rebuild();
            if (problem != null) schedule.Execute(() => overlay.Toast(problem));

            pump = schedule.Execute(Pump).Every(100);
            RegisterCallback<DetachFromPanelEvent>(_ => pump?.Pause());
            RegisterCallback<AttachToPanelEvent>(_ => pump?.Resume());
        }

        void Rebuild()
        {
            content.Clear();
            actionButtons.Clear();
            var column = Ui.Div(content, "le-page__column");

            BuildAccount(Ui.Div(column, "le-panel"));
            BuildNetwork(Ui.Div(column, "le-panel"));
            BuildCompute(Ui.Div(column, "le-panel"));
            BuildTool(Ui.Div(column, "le-panel"));
            BuildActions(Ui.Div(column, "le-panel"));

            var footer = Ui.Div(column, "le-settings__row le-page__footnote");
            Ui.Text(footer, $"Saved automatically to {path}. vacuumctl reads the same file.", "le-empty-hint le-settings__grow");
            Ui.Button(footer, "Show folder", null, () => FloorPlanFiles.Reveal(Path.GetDirectoryName(path)), "le-btn--ghost le-btn--small");

            RefreshProblems();
            RefreshRunning();
        }

        // ---------------------------------------------------------------- sections

        void BuildAccount(VisualElement panel)
        {
            PanelTitle(panel, IconKind.Logo, "Cloud connection",
                "Your organisation's own cloud account. The templates set up the resources there, and AWS bills you directly.");

            Ui.Text(panel, "Provider", "le-field-label");
            var available = new List<string>();
            foreach (var provider in CloudProviders.All) if (provider.available) available.Add(provider.label);
            panel.Add(new Segmented(available, 0));
            Ui.Text(panel, $"Planned: {CloudProviders.PlannedLabels}.", "le-empty-hint");

            var aws = connection.aws;
            Ui.Text(panel, "Sign-in method", "le-field-label le-panel__gap");
            var signIn = new Segmented(new[] { "AWS profile", "AWS SSO", "Credentials file" },
                Mathf.Max(0, Array.IndexOf(CloudConnection.SignInMethods, aws.signIn)));
            signIn.SelectionChanged += index =>
            {
                aws.signIn = CloudConnection.SignInMethods[index];
                Changed(rebuild: true);
            };
            panel.Add(signIn);

            string profileHint = aws.signIn == "sso"
                ? "The AWS SSO profile set up with `aws configure sso`. Sign in below when the session expires."
                : aws.signIn == "credentialsFile"
                    ? "The profile section to use inside the file ([default] if it has only one)."
                    : "A profile from your AWS configuration (`aws configure --profile <name>`).";
            TextRow(panel, "Profile", aws.profile, value => aws.profile = value.Trim(), profileHint);

            if (aws.signIn == "credentialsFile")
                TextRow(panel, "Credentials file", aws.credentialsFile, value => aws.credentialsFile = value.Trim(),
                    "Full path of a file in AWS credentials format. Only the path is saved; the keys stay in that file.", 1024);

            TextRow(panel, "Region", aws.region, value => aws.region = value.Trim().ToLowerInvariant(), "e.g. us-east-2");
            TextRow(panel, "Resource prefix", aws.resourcePrefix, value => aws.resourcePrefix = value.Trim().ToLowerInvariant(),
                "Starts every AWS resource name, so several teams can share an account (e.g. acme-vacuum).");

            Ui.Text(panel, "The simulator never stores keys or passwords: only the profile name or the file's path. " +
                           "Credentials stay in your AWS configuration (C.12).", "le-empty-hint le-panel__gap");
        }

        void BuildNetwork(VisualElement panel)
        {
            var net = connection.network;
            PanelTitle(panel, IconKind.Grid, "Network",
                "Where the simulation nodes run. They never accept inbound connections in any mode.");

            Ui.Text(panel, "Nodes run in", "le-field-label");
            var mode = new Segmented(new[] { "A new VPC per campaign", "Your existing VPC" }, connection.IsExistingVpc ? 1 : 0);
            mode.SelectionChanged += index =>
            {
                net.mode = CloudConnection.NetworkModes[index];
                Changed(rebuild: true);
            };
            panel.Add(mode);

            if (!connection.IsExistingVpc)
            {
                TextRow(panel, "VPC address range", net.vpcCidr, value => net.vpcCidr = value.Trim(),
                    "Private range, /16 to /20 (e.g. 10.42.0.0/16). Choose one that doesn't overlap a network you connect to.");

                var internet = new SwitchToggle("Nodes can reach the internet", net.nodeInternetAccess);
                internet.ValueChanged += value =>
                {
                    net.nodeInternetAccess = value;
                    Changed(rebuild: true);
                };
                internet.AddToClassList("le-panel__gap");
                panel.Add(internet);
                Ui.Text(panel, net.nodeInternetAccess
                        ? "Public subnets with an internet gateway. Simplest and cheapest. Custom algorithm code could make outbound connections."
                        : "Private network: no internet gateway. Nodes reach S3, ECR, ECS and CloudWatch Logs only through VPC " +
                          "endpoints, so custom algorithm code has no route out (4.8). AWS bills the endpoints per hour while a campaign runs.",
                    "le-empty-hint");
            }
            else
            {
                TextRow(panel, "VPC ID", net.existingVpcId, value => net.existingVpcId = value.Trim(), "e.g. vpc-0abc1234");
                TextRow(panel, "Subnet IDs", string.Join(", ", net.existingSubnetIds),
                    value => net.existingSubnetIds = CloudConnection.ParseList(value),
                    "Comma-separated; two in different zones is best. They must reach S3, ECR, ECS and CloudWatch Logs " +
                    "(a NAT gateway or VPC endpoints). Your network's routing decides internet access. Test connection checks it.", 512);
            }

            TextRow(panel, "HTTPS proxy", net.httpsProxy, value => net.httpsProxy = value.Trim(),
                "Optional, for corporate networks: this computer's proxy for calls to AWS, e.g. http://proxy.example.com:8080. " +
                "No user name or password here.", 256);
        }

        void BuildCompute(VisualElement panel)
        {
            var compute = connection.compute;
            PanelTitle(panel, IconKind.Sliders, "Compute",
                "Nodes start when a campaign starts and are removed when it ends (5.5). These limits cap the cost.");

            Ui.Text(panel, "Machines", "le-field-label");
            var provisioning = new Segmented(new[] { "Spot (cheaper, may be interrupted)", "On-demand" },
                compute.provisioning == "EC2" ? 1 : 0);
            provisioning.SelectionChanged += index =>
            {
                compute.provisioning = index == 1 ? "EC2" : "SPOT";
                Changed();
            };
            panel.Add(provisioning);
            Ui.Text(panel, "An interrupted Spot node is retried and carries on from its last finished run.", "le-empty-hint");

            Ui.Text(panel, "Maximum vCPUs at once", "le-field-label le-panel__gap");
            var vcpus = new ValueSlider(1f, 512f, Mathf.Clamp(compute.maxVCpus, 1, 512), v => $"{Mathf.Round(v):0} vCPUs", true);
            vcpus.ValueChanged += value =>
            {
                compute.maxVCpus = Mathf.Clamp(Mathf.RoundToInt(value), 1, CloudConnection.MaxVCpusLimit);
                Changed();
            };
            panel.Add(vcpus);

            Ui.Text(panel, "Time limit per node", "le-field-label le-panel__gap");
            var hours = new ValueSlider(1f, 48f, Mathf.Clamp(compute.nodeTimeoutSeconds / 3600f, 1f, 48f), v => $"{Mathf.Round(v):0} h", true);
            hours.ValueChanged += value =>
            {
                compute.nodeTimeoutSeconds = Mathf.RoundToInt(Mathf.Round(value) * 3600f);
                Changed();
            };
            panel.Add(hours);
            Ui.Text(panel, "AWS stops a node after this long, whatever it is doing (hard timeout, SDD 8).", "le-empty-hint");

            TextRow(panel, "Instance types", compute.instanceTypes, value => compute.instanceTypes = value.Replace(" ", string.Empty),
                "Optional, comma-separated (e.g. c6i.large,c7i.large). Empty = the template's mix of compute-optimised types.");
        }

        void BuildTool(VisualElement panel)
        {
            var tool = connection.tool;
            PanelTitle(panel, IconKind.Help, "Command-line tool",
                "The Settings page runs the vacuumctl tool for everything that talks to AWS. It needs Python 3.10+ with boto3.");

            TextRow(panel, "Python command", tool.python, value => tool.python = value.Trim(),
                $"Empty = {VacuumctlProcess.PythonCommand(new CloudConnection())}.", rebuild: true);
            TextRow(panel, "Cloud folder", tool.cloudFolder, value => tool.cloudFolder = value.Trim(),
                "The repository's cloud folder. Empty = found automatically.", 1024, rebuild: true);

            string folder = VacuumctlProcess.FindCloudFolder(connection, out string problem);
            Ui.Text(panel, folder != null ? $"Using {folder}" : problem, folder != null ? "le-empty-hint" : "le-empty-hint le-settings__warn");
        }

        void BuildActions(VisualElement panel)
        {
            PanelTitle(panel, IconKind.Check, "Account",
                "Check the connection, set up the cloud resources once, or remove everything again.");

            problemsLabel = Ui.Text(panel, string.Empty, "le-empty-hint le-settings__warn");

            var row = Ui.Div(panel, "le-settings__actions");
            actionButtons.Add(Ui.Button(row, "Test connection", IconKind.Check, TestConnection, "le-btn--primary"));
            if (connection.aws.signIn == "sso")
                actionButtons.Add(Ui.Button(row, "Sign in (SSO)", null, () => Run("SSO sign-in", new List<string> { "sso-login" }), "le-btn--ghost"));
            actionButtons.Add(Ui.Button(row, "Set up cloud resources", IconKind.Plus, ConfirmSetUp, "le-btn--ghost"));
            actionButtons.Add(Ui.Button(row, "Save admin policy…", IconKind.Save, SaveAdminPolicy, "le-btn--ghost"));
            actionButtons.Add(Ui.Button(row, "Remove all cloud resources", IconKind.Trash, ConfirmRemove, "le-btn--danger-ghost"));

            Ui.Text(panel, "No admin rights? Save the admin policy and ask your IT admin to create it as a customer managed policy " +
                           "and attach it to your user or role (it is too big for an inline policy). It allows only what setup and " +
                           "campaigns need, under your prefix.",
                "le-empty-hint");

            var status = Ui.Div(panel, "le-settings__row le-panel__gap");
            runningLabel = Ui.Text(status, string.Empty, "le-empty-hint le-settings__grow");
            cancelButton = Ui.Button(status, "Stop", IconKind.Stop, () => running?.Kill(), "le-btn--ghost le-btn--small");

            checksBox = Ui.Div(panel, "le-settings__checks");
            outputBox = Ui.Div(panel, "le-settings__output");
            RenderReport();
            foreach (string line in output) AddOutputLine(line);
        }

        // ---------------------------------------------------------------- actions

        void TestConnection()
        {
            lastReport = null;
            RenderReport();
            Run("Test connection", new List<string> { "test-connection", "--json" }, captureStdout: true, onDone: process =>
            {
                lastReport = TestReport.Parse(process.StandardOutput);
                if (lastReport == null && process.ExitCode != 0)
                    overlay.Toast("The test couldn't run. See the output below.");
                RenderReport();
            });
        }

        void ConfirmSetUp()
        {
            string email = string.Empty;
            float budget = 20f;
            var body = Ui.Div(null, "le-batch-setup");
            Ui.Text(body, $"Creates the image registry, the results bucket and a monthly budget alert in {connection.aws.region}, " +
                          $"named {connection.aws.resourcePrefix}-…. They cost nothing while idle except stored results. " +
                          "Compute is created per campaign and removed after it.", "le-empty-hint");
            Ui.Text(body, "Budget alert email (optional)", "le-field-label le-panel__gap");
            var emailField = Ui.TextInput(body, email, value => email = value.Trim());
            emailField.maxLength = 254;
            Ui.Text(body, "Monthly budget", "le-field-label le-panel__gap");
            var slider = new ValueSlider(5f, 500f, budget, v => $"${Mathf.Round(v):0}", true);
            slider.ValueChanged += value => budget = Mathf.Round(value);
            body.Add(slider);

            overlay.ShowDialog("Set up cloud resources", connection.aws.resourcePrefix, body, null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Set up", ButtonStyle.Primary, () =>
                {
                    email = emailField.text?.Trim() ?? string.Empty;
                    var args = new List<string> { "bootstrap", "--monthly-budget", budget.ToString("0", CultureInfo.InvariantCulture) };
                    if (email.Length > 0) { args.Add("--budget-email"); args.Add(email); }
                    Run("Set up cloud resources", args);
                }));
        }

        void ConfirmRemove()
        {
            string prefix = connection.aws.resourcePrefix;
            string typed = string.Empty;
            var body = Ui.Div(null, "le-batch-setup");
            Ui.Text(body, $"This deletes every AWS resource named {prefix}-… in {connection.aws.region}: compute, the image " +
                          "registry, the budget alert, and ALL stored campaign results in the results bucket. It can't be undone.",
                "le-empty-hint");
            Ui.Text(body, $"Type {prefix} to confirm", "le-field-label le-panel__gap");
            var field = Ui.TextInput(body, typed, value => typed = value);

            overlay.ShowDialog("Remove all cloud resources", null, body, null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Remove everything", ButtonStyle.Danger, () =>
                {
                    typed = field.text ?? string.Empty;
                    if (typed.Trim() != prefix)
                    {
                        overlay.Toast($"Nothing removed: type {prefix} exactly to confirm.");
                        return;
                    }
                    Run("Remove all cloud resources", new List<string> { "destroy", "--confirm", prefix });
                }));
        }

        void SaveAdminPolicy()
        {
            string target = FloorPlanFiles.PickSavePath("Save admin policy", $"{connection.aws.resourcePrefix}-admin-policy", "json");
            if (target == null) return;
            Run("Save admin policy", new List<string> { "admin-policy", "--out", target }, onDone: process =>
            {
                if (process.ExitCode == 0)
                    overlay.Toast($"Admin policy saved to {target}", "Show", () => FloorPlanFiles.Reveal(Path.GetDirectoryName(target)));
            });
        }

        // ---------------------------------------------------------------- running vacuumctl

        Action<VacuumctlProcess> onDone;

        void Run(string title, List<string> arguments, bool captureStdout = false, Action<VacuumctlProcess> onDone = null)
        {
            if (running != null && !running.HasExited)
            {
                overlay.Toast($"{running.Title} is still running.");
                return;
            }

            var problems = connection.Validate();
            if (problems.Count > 0)
            {
                overlay.Toast("Fix the settings marked above first.");
                return;
            }

            output.Clear();
            outputBox?.Clear();
            AppendOutput($"> vacuumctl {VacuumctlProcess.JoinArguments(arguments)}");

            running = VacuumctlProcess.Start(connection, path, title, arguments, captureStdout, out string problem);
            if (running == null)
            {
                AppendOutput(problem);
                overlay.Toast(problem);
                RefreshRunning();
                return;
            }
            this.onDone = onDone;
            RefreshRunning();
        }

        void Pump()
        {
            if (running == null) return;
            running.Drain(AppendOutput);
            if (!running.HasExited) return;

            var finished = running;
            finished.Drain(AppendOutput);
            AppendOutput(finished.ExitCode == 0 ? $"{finished.Title}: done." : $"{finished.Title}: finished with errors (exit code {finished.ExitCode}).");
            running = null;
            var callback = onDone;
            onDone = null;
            RefreshRunning();
            callback?.Invoke(finished);
        }

        void AppendOutput(string line)
        {
            output.Add(line);
            if (output.Count > MaxOutputLines) output.RemoveAt(0);
            AddOutputLine(line);
        }

        void AddOutputLine(string line)
        {
            if (outputBox == null) return;
            Ui.Text(outputBox, line, "le-settings__output-line");
            while (outputBox.childCount > MaxOutputLines) outputBox.RemoveAt(0);
        }

        void RefreshRunning()
        {
            bool busy = running != null && !running.HasExited;
            foreach (var button in actionButtons) button.SetEnabled(!busy);
            if (runningLabel != null) runningLabel.text = busy ? $"{running.Title}… (this can take several minutes)" : string.Empty;
            if (cancelButton != null) Ui.SetVisible(cancelButton, busy);
        }

        void RenderReport()
        {
            if (checksBox == null) return;
            checksBox.Clear();
            if (lastReport == null) return;

            foreach (var item in lastReport.checks)
            {
                var row = Ui.Div(checksBox, $"le-settings__check le-settings__check--{item.status}");
                Ui.Text(row, item.status == "ok" ? "OK" : item.status == "warn" ? "Note" : "Problem", "le-settings__check-status");
                Ui.Text(row, item.name, "le-settings__check-name");
                Ui.Text(row, item.message, "le-settings__check-message");
            }
            Ui.Text(checksBox, lastReport.ok ? "Connection OK." : "The connection has problems (marked above).",
                lastReport.ok ? "le-empty-hint" : "le-empty-hint le-settings__warn");
        }

        // ---------------------------------------------------------------- settings changes

        void Changed(bool rebuild = false)
        {
            try
            {
                connection.Save(path);
            }
            catch (Exception exception)
            {
                overlay.Toast($"Couldn't save the cloud settings: {exception.Message}");
            }

            lastReport = null; // a test of the old settings no longer applies
            if (rebuild) Rebuild();
            else
            {
                RefreshProblems();
                RenderReport();
            }
        }

        void RefreshProblems()
        {
            if (problemsLabel == null) return;
            var problems = connection.Validate();
            problemsLabel.text = problems.Count == 0 ? string.Empty : "Fix first: " + string.Join(" ", problems);
            Ui.SetVisible(problemsLabel, problems.Count > 0);
        }

        void TextRow(VisualElement panel, string label, string value, Action<string> store, string hint, int maxLength = 128,
            bool rebuild = false)
        {
            var row = Ui.Div(panel, "le-settings__row le-panel__gap");
            Ui.Text(row, label, "le-field-label le-settings__label");
            var field = Ui.TextInput(row, value ?? string.Empty, text =>
            {
                store(text ?? string.Empty);
                Changed(rebuild);
            }, "le-settings__grow");
            field.maxLength = maxLength;
            if (!string.IsNullOrEmpty(hint)) Ui.Text(panel, hint, "le-empty-hint");
        }

        static void PanelTitle(VisualElement panel, IconKind icon, string title, string text)
        {
            var top = Ui.Div(panel, "le-panel__header");
            var tile = Ui.Div(top, "le-start__step-icon");
            tile.Add(new IconElement(icon));

            var titles = Ui.Div(top, "le-panel__titles");
            Ui.Text(titles, title, "le-panel__title");
            Ui.Text(titles, text, "le-panel__text");
        }

        // Statics survive entering Play Mode when domain reload is off.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            running = null;
            output.Clear();
            lastReport = null;
        }

        /// <summary>vacuumctl test-connection --json, as JsonUtility reads it.</summary>
        [Serializable]
        public sealed class TestReport
        {
            public bool ok;
            public string provider;
            public string account;
            public string arn;
            public List<Check> checks = new List<Check>();

            [Serializable]
            public sealed class Check
            {
                public string name;
                public string status;
                public string message;
            }

            /// <summary>The report, or null if <paramref name="json"/> isn't one.</summary>
            public static TestReport Parse(string json)
            {
                if (string.IsNullOrWhiteSpace(json)) return null;
                try
                {
                    var report = JsonUtility.FromJson<TestReport>(json);
                    return report != null && report.checks != null && report.checks.Count > 0 ? report : null;
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
        }
    }
}
