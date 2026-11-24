using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using RobotVacuum.Level;
using RobotVacuum.Network;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The Settings page's network worker sections (requirement 3.9). "This PC as a worker" starts and controls
    /// this computer's own worker (a separate headless process) and shows its pairing code; "Network workers" lists
    /// the workers this PC can send batches to, adds one with its address and pairing code, and removes one. Nothing
    /// here needs any other tool: only the simulator on each computer.
    /// </summary>
    public sealed class NetworkWorkersView : VisualElement
    {
        sealed class Check
        {
            public bool busy;
            public string state; // "ok", "warn", "fail"
            public string text;
        }

        // Kept across page rebuilds: the last check of each worker, and the outcome of a pairing that is in progress.
        static readonly Dictionary<string, Check> checks = new Dictionary<string, Check>();
        static string pendingMessage;
        static int pairingsInProgress;

        readonly OverlayLayer overlay;
        readonly NetworkSettings settings;
        readonly string executableProblem;
        VisualElement localStatus, localActions, localLists, workerList;
        string localKey, remoteKey;

        public NetworkWorkersView(OverlayLayer overlay)
        {
            this.overlay = overlay;
            settings = WorkerRegistry.Settings();
            SimulatorExecutable.Find(out executableProblem);

            BuildLocal(Ui.Div(this, "le-panel"));
            BuildRemote(Ui.Div(this, "le-panel"));

            var pump = schedule.Execute(Refresh).Every(1000);
            RegisterCallback<DetachFromPanelEvent>(_ => pump.Pause());
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                pump.Resume();
                CheckAll();
            });
            Refresh();
        }

        // ---------------------------------------------------------------- this PC as a worker

        void BuildLocal(VisualElement panel)
        {
            Title(panel, IconKind.Grid, "This PC as a worker",
                "Lets PCs you pair with this one send it batches over your network. The worker runs as its own process while the simulator is open.");

            localStatus = Ui.Div(panel);
            localActions = Ui.Div(panel, "le-settings__actions");

            var row = Ui.Div(panel, "le-settings__row le-panel__gap");
            Ui.Text(row, "Port", "le-field-label le-settings__label");
            var port = Ui.TextInput(row, settings.workerPort.ToString(CultureInfo.InvariantCulture), text =>
            {
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 1024 && value <= 65535)
                {
                    settings.workerPort = value;
                    Save();
                }
                else overlay.Toast("A port is a number from 1024 to 65535. The worker's default is 47815.");
            }, "le-settings__grow");
            port.maxLength = 5;

            Ui.Text(panel, "Simulations at once on this PC", "le-field-label le-panel__gap");
            int cores = Math.Max(1, Environment.ProcessorCount);
            var processes = new ValueSlider(1f, cores, Mathf.Clamp(settings.Processes, 1, cores), v => $"{Mathf.Round(v):0} of {cores} CPU threads", true);
            processes.ValueChanged += value =>
            {
                settings.processes = Mathf.Clamp(Mathf.RoundToInt(value), 1, cores);
                Save();
            };
            panel.Add(processes);
            Ui.Text(panel, "Used by this PC's worker, and for this PC's own share of a network batch. Port and count apply the next time the worker starts. " +
                           "The first time, Windows asks whether the simulator may use the network: allow it on private networks.", "le-empty-hint");

            localLists = Ui.Div(panel);
        }

        void RefreshLocal()
        {
            var status = LocalWorker.Status();
            string state = status?.state ?? "stopped";
            bool pairing = state == "running" && !string.IsNullOrEmpty(status.pairingCode);
            string remaining = pairing ? Remaining(status.pairingExpiresUtc) : "";
            string key = state + "|" + status?.error + "|" + status?.pairingCode + "|" + remaining + "|" + status?.runningProcesses + "|" +
                         status?.queuedShards + "|" + string.Join(",", status?.clients?.Select(c => c.clientId + c.lastSeenUtc) ?? new string[0]) + "|" +
                         string.Join(",", status?.jobs?.Select(j => j.campaign + j.state + j.completedRuns) ?? new string[0]);
            if (key == localKey) return;
            localKey = key;

            localStatus.Clear();
            localActions.Clear();
            localLists.Clear();

            switch (state)
            {
                case "running":
                {
                    string addresses = status.addresses != null && status.addresses.Count > 0
                        ? string.Join(" or ", status.addresses.Select(a => a + (status.port == WorkerProtocol.DefaultPort ? "" : ":" + status.port)))
                        : "(no network connection found)";
                    Ui.Text(localStatus, $"On. Address to enter on the other PC: {addresses}. " +
                                         $"{status.runningProcesses} of {status.maxProcesses} simulation(s) busy, {status.queuedShards} waiting.", "le-empty-hint");
                    if (pairing)
                    {
                        Ui.Text(localStatus, status.pairingCode, "le-network__code");
                        Ui.Text(localStatus, $"Pairing code. On the other PC: Settings > Network workers > Add worker. Valid for {remaining}, for one PC.", "le-empty-hint");
                        Ui.Button(localActions, "Stop pairing", null, () => LocalWorker.Send("close-pairing"), "le-btn--ghost");
                    }
                    else Ui.Button(localActions, "Allow a new PC to pair", IconKind.Plus, () => LocalWorker.Send("open-pairing", minutes: WorkerProtocol.PairingMinutes), "le-btn--primary");
                    Ui.Button(localActions, "Stop worker", IconKind.Stop, () => ConfirmStop(status), "le-btn--danger-ghost");
                    BuildLocalLists(status);
                    break;
                }
                case "starting":
                    Ui.Text(localStatus, "Starting...", "le-empty-hint");
                    Ui.Button(localActions, "Stop worker", IconKind.Stop, LocalWorker.Stop, "le-btn--ghost");
                    break;
                default:
                    if (state == "error" && !string.IsNullOrEmpty(status?.error))
                        Ui.Text(localStatus, "The worker stopped: " + status.error, "le-empty-hint le-settings__warn");
                    else Ui.Text(localStatus, "Off.", "le-empty-hint");
                    if (executableProblem != null) Ui.Text(localStatus, executableProblem, "le-empty-hint le-settings__warn");
                    Ui.Button(localActions, "Start worker", IconKind.Play, StartWorker, "le-btn--primary");
                    break;
            }
        }

        void BuildLocalLists(WorkerStatusFile status)
        {
            Ui.Text(localLists, "Paired PCs", "le-field-label le-panel__gap");
            var clients = Ui.Div(localLists, "le-network__list");
            if (status.clients == null || status.clients.Count == 0)
                Ui.Text(clients, "None yet. Choose 'Allow a new PC to pair', then enter the code on the other PC.", "le-empty-hint");
            else
                foreach (var client in status.clients.OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase))
                {
                    var row = Ui.Div(clients, "le-network__row");
                    Ui.Div(row, "le-network__dot le-network__dot--ok");
                    Ui.Text(row, client.name, "le-network__row-name");
                    Ui.Text(row, $"last seen {When(client.lastSeenUtc)}{(string.IsNullOrEmpty(client.lastAddress) ? "" : " from " + client.lastAddress)}", "le-network__row-text");
                    var captured = client;
                    Ui.Button(row, "Remove", IconKind.Trash, () => ConfirmUnpair(captured), "le-btn--ghost le-settings__inline-btn");
                }

            if (status.jobs == null || status.jobs.Count == 0) return;
            Ui.Text(localLists, "Batches on this worker", "le-field-label le-panel__gap");
            var jobs = Ui.Div(localLists, "le-network__list");
            foreach (var job in status.jobs)
            {
                var row = Ui.Div(jobs, "le-network__row");
                Ui.Div(row, "le-network__dot " + DotFor(job.state));
                Ui.Text(row, job.client, "le-network__row-name");
                Ui.Text(row, $"{job.campaign}: {job.state}, {job.completedRuns} of {job.totalRuns} run(s)", "le-network__row-text");
            }
        }

        void StartWorker()
        {
            if (!LocalWorker.Start(settings, out string problem)) overlay.Toast(problem);
            else overlay.Toast("Starting the worker. Then choose 'Allow a new PC to pair'.");
        }

        void ConfirmStop(WorkerStatusFile status)
        {
            if (status.runningProcesses == 0 && status.queuedShards == 0)
            {
                LocalWorker.Stop();
                return;
            }
            overlay.ShowDialog("Stop the worker?",
                $"{status.runningProcesses} simulation(s) from other PCs are running here and {status.queuedShards} are waiting. They stop now. " +
                "When the worker starts again they continue from their last finished run, unless the other PC has moved them elsewhere by then.",
                null,
                new DialogButton("Keep running", ButtonStyle.Ghost, null),
                new DialogButton("Stop worker", ButtonStyle.Danger, LocalWorker.Stop));
        }

        void ConfirmUnpair(PairedClient client)
        {
            overlay.ShowDialog($"Remove {client.name}?",
                "It can no longer send batches to this PC until it pairs again, and the batches it sent here are stopped and deleted.",
                null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Remove", ButtonStyle.Danger, () => LocalWorker.Send("unpair", client.clientId)));
        }

        // ---------------------------------------------------------------- workers this PC can use

        void BuildRemote(VisualElement panel)
        {
            Title(panel, IconKind.Chart, "Network workers",
                "Computers running the simulator as a worker. A batch can be split across them and this PC: Start simulation > Batch > Run on.");

            workerList = Ui.Div(panel, "le-network__list");

            var actions = Ui.Div(panel, "le-settings__actions");
            Ui.Button(actions, "Add worker", IconKind.Plus, ShowAddWorker, "le-btn--primary");
            Ui.Button(actions, "Check all", IconKind.Check, CheckAll, "le-btn--ghost");

            Ui.Text(panel, "To add a computer: install the same version of the simulator on it, open its Settings, choose Start worker and then " +
                           "'Allow a new PC to pair'. Press Add worker here and enter the address and code it shows.", "le-empty-hint");
            if (Application.isEditor)
                Ui.Text(panel, "This simulator is running in the Unity editor. A worker refuses a different version number, but it can't tell whether a " +
                               "build with the same number has different code: build every computer's copy from the same commit (2.1).", "le-empty-hint le-settings__warn");
        }

        void RefreshRemote()
        {
            var workers = WorkerRegistry.Workers();
            string key;
            lock (checks)
                key = pairingsInProgress + "|" + string.Join(",", workers.Select(w =>
                {
                    checks.TryGetValue(w.workerId, out var check);
                    return w.workerId + w.host + w.port + (check == null ? "-" : check.busy + check.state + check.text);
                }));
            if (key == remoteKey) return;
            remoteKey = key;

            workerList.Clear();
            if (workers.Count == 0)
            {
                Ui.Text(workerList, pairingsInProgress > 0 ? "Pairing..." : "No workers yet.", "le-empty-hint");
                return;
            }
            foreach (var worker in workers.OrderBy(w => w.name, StringComparer.OrdinalIgnoreCase))
            {
                Check check;
                lock (checks) checks.TryGetValue(worker.workerId, out check);
                var row = Ui.Div(workerList, "le-network__row");
                Ui.Div(row, "le-network__dot " + (check == null || check.busy ? "" : "le-network__dot--" + check.state));
                Ui.Text(row, worker.name, "le-network__row-name");
                string text = check == null ? "Not checked yet." : check.busy ? "Checking..." : check.text;
                Ui.Text(row, $"{worker.Address} · {text}", "le-network__row-text");
                var captured = worker;
                Ui.Button(row, "Remove", IconKind.Trash, () => ConfirmRemoveWorker(captured), "le-btn--ghost le-settings__inline-btn");
            }
        }

        void CheckAll()
        {
            foreach (var worker in WorkerRegistry.Workers()) CheckWorker(worker);
        }

        static void CheckWorker(PairedWorker worker)
        {
            lock (checks)
            {
                if (checks.TryGetValue(worker.workerId, out var existing) && existing.busy) return;
                checks[worker.workerId] = new Check { busy = true, state = existing?.state, text = existing?.text };
            }
            new Thread(() =>
            {
                var result = new Check();
                try
                {
                    using (var client = WorkerRegistry.Connect(worker))
                    {
                        var info = client.Info;
                        result.state = "ok";
                        result.text = $"Online: {info.maxProcesses} simulation(s) at a time, {info.runningProcesses} busy now.";
                    }
                }
                catch (WorkerException exception)
                {
                    result.state = exception.Code == WorkerProtocol.ErrorNotPaired || exception.Code == WorkerProtocol.ErrorVersion ? "fail" : "warn";
                    result.text = exception.Message;
                }
                catch (Exception exception)
                {
                    result.state = "warn";
                    result.text = exception.Message;
                }
                lock (checks) checks[worker.workerId] = result;
            }) { IsBackground = true, Name = "Worker check" }.Start();
        }

        void ShowAddWorker()
        {
            var body = Ui.Div(null, "le-batch-setup");
            Ui.Text(body, "Worker address", "le-field-label");
            var address = Ui.TextInput(body, string.Empty, null);
            Ui.Text(body, "As shown on the worker, e.g. 192.168.1.20 (add :port if it isn't 47815).", "le-empty-hint");
            Ui.Text(body, "Pairing code", "le-field-label le-panel__gap");
            var code = Ui.TextInput(body, string.Empty, null);
            code.maxLength = 16;
            Ui.Text(body, "The 10-character code the worker shows after 'Allow a new PC to pair'. It is never sent over the network: " +
                          "both computers check it, and then agree a key that stays in each one's Windows Credential Manager.", "le-empty-hint");

            overlay.ShowDialog("Add a network worker", null, body, null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Pair", ButtonStyle.Primary, () =>
                {
                    string addressText = address.text?.Trim() ?? "";
                    string codeText = code.text ?? "";
                    if (!WorkerProtocol.TryParseAddress(addressText, out _, out _, out string problem))
                    {
                        overlay.Toast(problem);
                        return;
                    }
                    Interlocked.Increment(ref pairingsInProgress);
                    overlay.Toast($"Pairing with {addressText}...");
                    new Thread(() =>
                    {
                        string message;
                        try
                        {
                            var worker = WorkerRegistry.Pair(addressText, codeText);
                            message = $"Paired with {worker.name}. It can now run batches for this PC.";
                            CheckWorker(worker);
                        }
                        catch (WorkerException exception)
                        {
                            message = exception.Message;
                        }
                        catch (Exception exception)
                        {
                            message = "Pairing failed: " + exception.Message;
                        }
                        Interlocked.Decrement(ref pairingsInProgress);
                        Volatile.Write(ref pendingMessage, message);
                    }) { IsBackground = true, Name = "Worker pairing" }.Start();
                }));
        }

        void ConfirmRemoveWorker(PairedWorker worker)
        {
            overlay.ShowDialog($"Remove {worker.name}?",
                "This PC can't send it batches until you pair again. If it can be reached, it forgets this PC too and deletes the batches this PC left there.",
                null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Remove", ButtonStyle.Danger, () =>
                {
                    new Thread(() =>
                    {
                        string note = WorkerRegistry.Remove(worker);
                        lock (checks) checks.Remove(worker.workerId);
                        Volatile.Write(ref pendingMessage, note ?? $"Removed {worker.name}.");
                    }) { IsBackground = true, Name = "Worker removal" }.Start();
                }));
        }

        // ---------------------------------------------------------------- shared

        void Refresh()
        {
            string message = Interlocked.Exchange(ref pendingMessage, null);
            if (message != null) overlay.Toast(message);
            RefreshLocal();
            RefreshRemote();
        }

        void Save()
        {
            try { WorkerRegistry.SaveSettings(settings); }
            catch (Exception exception) { overlay.Toast($"Couldn't save the network settings: {exception.Message}"); }
        }

        static string DotFor(string state) =>
            state == WorkState.Finished || state == WorkState.Running ? "le-network__dot--ok"
            : state == WorkState.Failed || state == WorkState.Rejected ? "le-network__dot--fail"
            : "le-network__dot--warn";

        static string When(string utc) =>
            DateTime.TryParse(utc, null, DateTimeStyles.RoundtripKind, out DateTime time) ? time.ToLocalTime().ToString("g") : "never";

        static string Remaining(string utc)
        {
            if (!DateTime.TryParse(utc, null, DateTimeStyles.RoundtripKind, out DateTime until)) return "a few minutes";
            var left = until.ToUniversalTime() - DateTime.UtcNow;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return $"{(int)left.TotalMinutes}:{left.Seconds:00}";
        }

        static void Title(VisualElement panel, IconKind icon, string title, string text)
        {
            var top = Ui.Div(panel, "le-panel__header");
            var tile = Ui.Div(top, "le-start__step-icon");
            tile.Add(new IconElement(icon));
            var titles = Ui.Div(top, "le-panel__titles");
            Ui.Text(titles, title, "le-panel__title");
            Ui.Text(titles, text, "le-panel__text");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            lock (checks) checks.Clear();
            pendingMessage = null;
            pairingsInProgress = 0;
        }
    }

    /// <summary>
    /// The strip at the bottom right of the start screen while a network batch runs: progress, Details (each
    /// computer's share) and Cancel (confirmed, C.9). When the batch ends it says so, with a way to open the results.
    /// </summary>
    public sealed class NetworkBatchBar : VisualElement
    {
        static string dismissedCampaign;

        readonly OverlayLayer overlay;
        readonly Action<string> openRun;
        readonly Label text;
        readonly VisualElement buttons;
        string key;

        public NetworkBatchBar(OverlayLayer overlay, Action<string> openRun)
        {
            this.overlay = overlay;
            this.openRun = openRun;
            AddToClassList("le-netbar");
            text = Ui.Text(this, string.Empty, "le-netbar__text");
            buttons = Ui.Div(this, "le-netbar__buttons");
            var pump = schedule.Execute(Refresh).Every(1000);
            RegisterCallback<DetachFromPanelEvent>(_ => pump.Pause());
            RegisterCallback<AttachToPanelEvent>(_ => pump.Resume());
            Refresh();
        }

        void Refresh()
        {
            var campaign = NetworkCampaign.Current;
            bool show = campaign != null && dismissedCampaign != campaign.CampaignId;
            Ui.SetVisible(this, show);
            if (!show) return;

            var progress = campaign.Progress();
            string line;
            switch (progress.phase)
            {
                case NetworkCampaign.PhaseStarting: line = "Network batch: connecting..."; break;
                case NetworkCampaign.PhaseRunning:
                    line = $"Network batch: {progress.completedRuns} of {progress.totalRuns} runs on {progress.targets.Count(t => t.state != "unreachable" && t.state != "lost")} computer(s)";
                    break;
                case NetworkCampaign.PhaseCollecting:
                case NetworkCampaign.PhaseImporting: line = "Network batch: bringing the results into Analysis..."; break;
                case NetworkCampaign.PhaseFinished: line = $"Network batch finished: {campaign.Imported?.imported ?? 0} run(s) in Analysis"; break;
                case NetworkCampaign.PhaseCancelled: line = "Network batch cancelled"; break;
                default: line = "Network batch stopped"; break;
            }
            string newKey = progress.phase + line;
            text.text = line;
            if (newKey == key) return;
            key = newKey;

            buttons.Clear();
            Ui.Button(buttons, "Details", null, () => ShowDetails(campaign), "le-btn--ghost le-settings__inline-btn");
            if (!campaign.IsOver)
                Ui.Button(buttons, "Cancel", IconKind.Stop, () => ConfirmCancel(campaign), "le-btn--danger-ghost le-settings__inline-btn");
            else
            {
                if (campaign.Phase == NetworkCampaign.PhaseFinished)
                    Ui.Button(buttons, "Open", IconKind.Chart, () =>
                    {
                        dismissedCampaign = campaign.CampaignId;
                        openRun(CampaignImport.RunIdFor(campaign.CampaignId, 0));
                    }, "le-btn--primary le-settings__inline-btn");
                Ui.IconButton(buttons, IconKind.Close, "Hide", () =>
                {
                    dismissedCampaign = campaign.CampaignId;
                    Refresh();
                }, "le-btn--ghost");
            }
        }

        void ConfirmCancel(NetworkCampaign campaign)
        {
            overlay.ShowDialog("Cancel the network batch?",
                "Every computer stops its share now. The runs that already came back are kept in the campaign folder, and you can bring them " +
                "into Analysis with Import results.",
                null,
                new DialogButton("Keep running", ButtonStyle.Ghost, null),
                new DialogButton("Cancel batch", ButtonStyle.Danger, campaign.Cancel));
        }

        void ShowDetails(NetworkCampaign campaign)
        {
            var progress = campaign.Progress();
            var body = Ui.Div(null, "le-batch-setup");
            if (!string.IsNullOrEmpty(progress.problem)) Ui.Text(body, progress.problem, "le-empty-hint le-settings__warn");
            var list = Ui.Div(body, "le-network__list");
            foreach (var target in progress.targets)
            {
                var row = Ui.Div(list, "le-network__row");
                string dot = target.state == "unreachable" || target.state == "lost" ? "fail" : target.state == "reconnecting" ? "warn" : "ok";
                Ui.Div(row, "le-network__dot le-network__dot--" + dot);
                Ui.Text(row, target.label, "le-network__row-name");
                string detail = target.shards > 0 ? $"{target.completedRuns} of {target.totalRuns} runs, {target.shards} shard(s)" : "no shards";
                Ui.Text(row, $"{detail} · {target.state}{(string.IsNullOrEmpty(target.message) ? "" : ": " + target.message)}", "le-network__row-text");
            }
            Ui.Text(body, $"Everything about this batch, including every computer's log, is in {campaign.Folder}.", "le-empty-hint le-panel__gap");

            overlay.ShowDialog(campaign.CampaignName, $"{progress.completedRuns} of {progress.totalRuns} runs done, {progress.failedRuns} failed.", body, null,
                new DialogButton("Show folder", ButtonStyle.Ghost, () => FloorPlanFiles.Reveal(campaign.Folder)),
                new DialogButton("Close", ButtonStyle.Primary, null));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => dismissedCampaign = null;
    }
}
