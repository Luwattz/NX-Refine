using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using NXOpen;
using NXRefine.Analysis;
using NXRefine.Core;
using NXRefine.Repair;
using NXRefine.UI;

namespace NXRefine
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            Run(ResolveCommand(args));
        }

        public static void Run(string command)
        {
            NxContext context = NxContext.Current;
            try
            {
                CleanupSettings settings = CleanupSettings.Load();
                var analyzer = new GeometryAnalyzer(context, settings);
                var engine = new CleanupEngine(context, settings);

                switch (command)
                {
                    case "analyze": ShowAnalysis(context, analyzer); break;
                    case "simplify": RunSimplify(context, engine); break;
                    case "remove-blends": ReportCount(context, "small blend faces", engine.RemoveBlends()); break;
                    case "fill-holes": using (var form = new FillHolesDialog(context, settings)) form.ShowDialog(); break;
                    case "clear-cavities": using (var form = new ClearCavitiesDialog(context)) form.ShowDialog(); break;
                    case "repair-unattached": using (var form = new UnattachedFacesDialog(context, settings)) form.ShowDialog(); break;
                    case "remove-small": ReportCount(context, "small faces", engine.RemoveSmallFaces()); break;
                    case "repair-sheets": ReportCount(context, "sheet bodies", engine.RepairSheets()); break;
                    case "remove-markings": using (var form = new MarkingsDialog(context, settings)) form.ShowDialog(); break;
                    case "patch-openings": ShowOpeningGuidance(context, analyzer); break;
                    case "settings": using (var form = new SettingsForm(settings)) form.ShowDialog(); break;
                    case "about": ShowAbout(); break;
                    default: ShowAbout(); break;
                }
            }
            catch (Exception ex)
            {
                context.Log(ex.ToString());
                MessageBox.Show(ex.Message, "NX Refine", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static int GetUnloadOption(string dummy) { return (int)Session.LibraryUnloadOption.Immediately; }

        private static string ResolveCommand(string[] args)
        {
            if (args != null)
            {
                foreach (string arg in args)
                {
                    string normalized = (arg ?? string.Empty).Trim().ToLowerInvariant();
                    if (normalized.Contains("nx_refine_analyze") || normalized == "analyze") return "analyze";
                    if (normalized.Contains("nx_refine_simplify") || normalized == "simplify") return "simplify";
                    if (normalized.Contains("nx_refine_remove_blends") || normalized == "remove-blends") return "remove-blends";
                    if (normalized.Contains("nx_refine_fill_holes") || normalized == "fill-holes") return "fill-holes";
                    if (normalized.Contains("nx_refine_clear_cavities") || normalized == "clear-cavities") return "clear-cavities";
                    if (normalized.Contains("nx_refine_repair_unattached") || normalized == "repair-unattached") return "repair-unattached";
                    if (normalized.Contains("nx_refine_remove_small") || normalized == "remove-small") return "remove-small";
                    if (normalized.Contains("nx_refine_repair_sheets") || normalized == "repair-sheets") return "repair-sheets";
                    if (normalized.Contains("nx_refine_remove_markings") || normalized == "remove-markings") return "remove-markings";
                    if (normalized.Contains("nx_refine_patch_openings") || normalized == "patch-openings") return "patch-openings";
                    if (normalized.Contains("nx_refine_settings") || normalized == "settings") return "settings";
                    if (normalized.Contains("nx_refine_about") || normalized == "about") return "about";
                }
            }
            return "about";
        }

        private static void ShowAnalysis(NxContext context, GeometryAnalyzer analyzer)
        {
            IList<Issue> issues = analyzer.Analyze(true);
            context.Log("Geometry analysis completed.", true);
            foreach (IGrouping<IssueKind, Issue> group in issues.GroupBy(issue => issue.Kind).OrderBy(group => group.Key.ToString()))
                context.Log(group.Key + ": " + group.Count(), true);
            MessageBox.Show(
                issues.Count == 0 ? "No issues were found by the enabled checks." : issues.Count + " issue references were found and highlighted. See the Listing Window for the category summary.",
                "NX Refine - Analysis",
                MessageBoxButtons.OK,
                issues.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private static void RunSimplify(NxContext context, CleanupEngine engine)
        {
            int blends = engine.RemoveBlends();
            int holes = engine.FillHoles();
            int faces = engine.RemoveSmallFaces();
            context.Log("Simplify completed: " + blends + " blend faces, " + holes + " hole faces, " + faces + " small faces processed.");
        }

        private static void ShowOpeningGuidance(NxContext context, GeometryAnalyzer analyzer)
        {
            int openings = analyzer.Analyze(true).Count(issue => issue.Kind == IssueKind.OpenSheetBoundary);
            context.Log("Open sheet boundary references: " + openings);
            MessageBox.Show(
                openings + " open sheet boundary references were highlighted. Automatic N-sided and mesh patch generation is intentionally preview-only in this release; use Repair Sheets for gaps within sewing tolerance.",
                "NX Refine - Patch Openings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private static void ReportCount(NxContext context, string label, int count)
        {
            context.Log("Processed " + count + " " + label + ".");
        }

        private static void ShowAbout()
        {
            MessageBox.Show(
                "NX Refine 0.1.0" + Environment.NewLine +
                "Open-source geometry validation and cleanup tools for Siemens NX." + Environment.NewLine + Environment.NewLine +
                "Always validate repaired geometry before simulation or manufacturing.",
                "About NX Refine",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }
}
