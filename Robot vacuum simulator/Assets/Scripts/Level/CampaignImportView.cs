using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The Analysis page's "Import results" dialog: lists the campaign results found on disk (cloud campaigns fetched
    /// with vacuumctl, and local headless campaigns) and imports the one picked into the run history
    /// (<see cref="CampaignImport"/>). Reads local files only.
    /// </summary>
    public static class CampaignImportView
    {
        /// <summary>Shows the dialog. <paramref name="imported"/> gets the first imported batch's id, to select it.</summary>
        public static void Show(OverlayLayer overlay, Action<string> imported)
        {
            var roots = CampaignImport.DefaultRoots();
            var campaigns = CampaignImport.Find(roots, RunHistory.LoadAll());

            var body = Ui.Div(null, "le-batch-setup");
            if (campaigns.Count == 0)
            {
                string where = roots.Count > 0 ? string.Join(" or ", roots) : "the repository's cloud folder (set it under Network Settings › Command-line tool)";
                Ui.Text(body, $"No campaign results found in {where}. Fetch a cloud campaign with python -m vacuumctl fetch <campaign id>, " +
                              "or run one on this computer with python -m vacuumctl local; its results land there.", "le-empty-hint");
            }
            else
            {
                Ui.Text(body, "Each campaign is listed in Analysis as one batch per floor plan and configuration, with the same " +
                              "statistics as batches run here. Importing again replaces the earlier import.", "le-empty-hint");
                var list = new ScrollView(ScrollViewMode.Vertical);
                list.AddToClassList("le-import__list");
                list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
                body.Add(list);
                foreach (var campaign in campaigns) list.Add(Row(overlay, campaign, imported));
            }

#if UNITY_EDITOR
            Ui.Button(body, "Browse for a campaign folder…", IconKind.Plus, () =>
            {
                string folder = UnityEditor.EditorUtility.OpenFolderPanel("Campaign results folder", roots.Count > 0 ? roots[0] : string.Empty, string.Empty);
                if (string.IsNullOrEmpty(folder)) return;
                overlay.CancelDialog();
                Run(overlay, folder, imported);
            }, "le-btn--ghost le-panel__gap");
#endif

            overlay.ShowDialog("Import campaign results", null, body, null, new DialogButton("Close", ButtonStyle.Ghost, null));
        }

        static VisualElement Row(OverlayLayer overlay, CampaignImport.Campaign campaign, Action<string> imported)
        {
            var row = Ui.Div(null, "le-run-row le-import__row");
            var text = Ui.Div(row, "le-run-row__text");
            Ui.Text(text, campaign.name, "le-run-row__title");
            Ui.Text(text, $"{campaign.SourceLabel} · {campaign.runs} runs" + (campaign.failed > 0 ? $" ({campaign.failed} failed)" : string.Empty) +
                          $" · {campaign.generated:d MMM HH:mm}" + (campaign.imported ? " · imported" : string.Empty), "le-run-row__meta");
            Ui.Button(row, campaign.imported ? "Import again" : "Import", null, () =>
            {
                overlay.CancelDialog();
                Run(overlay, campaign.folder, imported);
            }, campaign.imported ? "le-btn--ghost le-settings__inline-btn" : "le-btn--primary le-settings__inline-btn");
            return row;
        }

        static void Run(OverlayLayer overlay, string folder, Action<string> imported)
        {
            CampaignImport.Result result;
            try
            {
                result = CampaignImport.Import(folder);
            }
            catch (Exception exception)
            {
                overlay.Toast($"Couldn't import {Path.GetFileName(folder)}: {exception.Message}");
                return;
            }

            foreach (string problem in result.problems) Debug.LogWarning($"Campaign import: {problem}");
            if (result.imported == 0)
            {
                overlay.Toast(result.problems.Count > 0 ? result.problems[0] : "That campaign has no runs to import.");
                return;
            }

            string message = $"Imported {result.imported} runs as {result.batchIds.Count} batch(es)";
            if (result.failed > 0) message += $"; {result.failed} failed run(s) are listed but left out of the statistics";
            if (result.withoutDetails > 0) message += $"; {result.withoutDetails} without maps (fetch the campaign again to download them)";
            overlay.Toast(message + ".");
            imported?.Invoke(result.FirstBatchId);
        }
    }
}
