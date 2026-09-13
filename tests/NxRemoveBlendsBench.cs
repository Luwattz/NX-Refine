using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NXOpen;

// Runs the production deletion code in an isolated NX journal process.
// Never saves the input part; verifies current topology and restores the undo mark.
internal static class NxRemoveBlendsBench
{
    public static int Main(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("usage: bench <part> <NXRefine.dll> [minimumRemoved] [repeatCount] [minimumSecondRoundRemoved]");
        Session session = Session.GetSession();
        PartLoadStatus status;
        Part part = session.Parts.OpenDisplay(Path.GetFullPath(args[0]), out status);
        status.Dispose();
        Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(args[1]));
        Type type = assembly.GetType("NXRefine.UI.RemoveBlendsDialog", true);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        // Skip dialog construction: this test exercises the actual production
        // geometry methods without creating interactive Block Styler controls.
        object dialog = FormatterServices.GetUninitializedObject(type);
        object context = assembly.GetType("NXRefine.Core.NxContext", true)
            .GetProperty("Current").GetValue(null, null);
        type.GetField("context", flags).SetValue(dialog, context);
        type.GetField("activeMaxRadius", flags).SetValue(dialog, 3.0);
        MethodInfo recognize = type.GetMethod("TryGetBlend", flags);
        Face[] all = CurrentFaces(part);
        Face[] candidates = all.Where(face =>
        {
            object[] values = { face, 0.0, false };
            return (bool)recognize.Invoke(dialog, values) && (bool)values[2] &&
                (double)values[1] > 0 && (double)values[1] <= 3.0;
        }).ToArray();
        if (candidates.Length == 0) throw new Exception("Fixture must contain radius-filtered seeds.");
        int seedCount = candidates.Length;
        type.GetField("recognitionSeeds", flags).SetValue(dialog, candidates.Select(face => face.Tag).ToArray());
        MethodInfo groupMethod = type.GetMethod("BuildConnectedGroups", flags);
        Face[][] groups = (Face[][])groupMethod.Invoke(dialog, new object[] { candidates });
        candidates = groups.SelectMany(group => group).GroupBy(face => face.Tag).Select(group => group.First()).ToArray();
        Tag[] selected = candidates.Select(face => face.Tag).ToArray();
        if (selected.Length == 0) throw new Exception("Fixture must expose native connected chains.");
        // A seed subset is not a hard face boundary: NX may expand beyond it.
        bool expandedSingleSeed = false;
        foreach (Face seed in candidates.Take(100))
        {
            Face[][] expanded = (Face[][])groupMethod.Invoke(dialog, new object[] { new[] { seed } });
            if (expanded.SelectMany(group => group).Any(face => face.Tag != seed.Tag)) { expandedSingleSeed = true; break; }
        }
        if (!expandedSingleSeed) throw new Exception("Weak seed selection did not expand a connected chain.");
        Console.WriteLine("SEEDS=" + seedCount + " EXPANDED=" + selected.Length + " NATIVE_GROUPS=" + groups.Length + " WEAK_SEED_EXPANSION=PASS");
        HashSet<Tag> originalTags = new HashSet<Tag>(all.Select(face => face.Tag));
        int originalBodies = part.Bodies.ToArray().Length;
        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "Remove Blends regression");
        try
        {
            int rounds = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1;
            for (int round = 1; round <= rounds; round++)
            {
                if (round > 1)
                {
                    Face[] freshSeeds = CurrentFaces(part).Where(face =>
                    {
                        object[] values = { face, 0.0, false };
                        return (bool)recognize.Invoke(dialog, values) && (bool)values[2] &&
                            (double)values[1] > 0 && (double)values[1] <= 3.0;
                    }).ToArray();
                    type.GetField("recognitionSeeds", flags).SetValue(dialog, freshSeeds.Select(face => face.Tag).ToArray());
                    groups = (Face[][])groupMethod.Invoke(dialog, new object[] { freshSeeds });
                    selected = groups.SelectMany(group => group).Select(face => face.Tag).Distinct().ToArray();
                    Console.WriteLine("REPEAT_ROUND=" + round + " SEEDS=" + freshSeeds.Length + " PREVIEW=" + selected.Length);
                }
                Stopwatch timer = Stopwatch.StartNew();
                int reported = (int)type.GetMethod("DeleteBestEffort", flags).Invoke(dialog, new object[] { selected });
                HashSet<Tag> live = new HashSet<Tag>(CurrentFaces(part).Select(face => face.Tag));
                int removed = selected.Count(tag => !live.Contains(tag));
                Console.WriteLine("CANDIDATES=" + selected.Length + " REMOVED=" + removed + " REPORTED=" + reported +
                    " ATTEMPTS=" + type.GetField("deleteAttempts", flags).GetValue(dialog) +
                    " MS=" + timer.ElapsedMilliseconds + " BUDGET_REACHED=" + type.GetField("deleteBudgetReported", flags).GetValue(dialog));
                if (reported != removed) throw new Exception("Reported deletion count differs from current body topology.");
                int minimum = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 1;
                if (round == 1 && removed < minimum) throw new Exception("Deletion regression: expected at least " + minimum + " removed faces.");
                int secondMinimum = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 0;
                if (round == 2 && removed < secondMinimum)
                    throw new Exception("Second-round regression: expected at least " + secondMinimum + " removed faces.");
                if (part.Bodies.ToArray().Length != originalBodies || part.Bodies.ToArray().Any(body => !body.IsSolidBody))
                    throw new Exception("Deletion must retain the fixture's solid bodies.");
                Face[] resolved = (Face[])type.GetMethod("ResolveCurrentFaces", flags).Invoke(dialog, new object[] { selected });
                if (resolved.Length != selected.Length - removed) throw new Exception("Historical faces counted as current faces.");
            }
        }
        finally
        {
            session.UndoToMark(mark, null);
            session.DeleteUndoMark(mark, null);
        }
        if (!originalTags.SetEquals(CurrentFaces(part).Select(face => face.Tag)))
            throw new Exception("Undo did not restore the original topology.");
        Console.WriteLine("PASS: actual removals, current face resolution, solid bodies, undo; input not saved.");
        return 0;
    }

    private static Face[] CurrentFaces(Part part)
    {
        return part.Bodies.ToArray().SelectMany(body => body.GetFaces()).ToArray();
    }
}

