using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NXOpen;
using NXOpen.UF;
using NXOpen.Utilities;

// Isolated NX journal. Synthetic parts are never saved.
public static class NxClearCavitiesTests
{
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static UFSession uf;
    private static object dialog;
    private static MethodInfo detect, delete;
    private static object baseline;
    private static MethodInfo baselineDetect;

    public static void Main(string[] args)
    {
        Session session = Session.GetSession();
        uf = UFSession.GetUFSession();
        Assembly assembly = Assembly.LoadFrom(args[0]);
        Type type = assembly.GetType("NXRefine.UI.ClearCavitiesDialog", true);
        dialog = MakeDialog(assembly, type);
        detect = type.GetMethod("FindCavityGroups", Flags);
        delete = type.GetMethod("DeleteCavityFaces", Flags);
        if (args.Length > 1)
        {
            Assembly old = Assembly.LoadFrom(args[1]);
            Type oldType = old.GetType("NXRefine.UI.ClearCavitiesDialog", true);
            baseline = MakeDialog(old, oldType);
            baselineDetect = oldType.GetMethod("FindCavityGroups", Flags);
        }
        foreach (bool inches in new[] { false, true })
        {
            double s = inches ? 1.0 / 25.4 : 1.0;
            Part part = session.Parts.NewDisplay(Path.Combine(Path.GetDirectoryName(args[0]),
                "cavity_test_" + Guid.NewGuid().ToString("N") + ".prt"),
                inches ? Part.Units.Inches : Part.Units.Millimeters);
            Body plain = Body(Block(0, 0, 0, 20, 20, 20, s));
            Check(Find(plain).Length == 0, "Plain block has no cavities");
            Tag hollow = Block(30, 0, 0, 20, 20, 20, s);
            hollow = Subtract(hollow, Block(32, 2, 2, 3, 3, 3, s));
            Tag sphereFeature, sphere;
            uf.Modl.CreateSphere1(FeatureSigns.Nullsign, new[] { 42 * s, 12 * s, 12 * s },
                (4 * s).ToString("R", CultureInfo.InvariantCulture), out sphereFeature);
            uf.Modl.AskFeatBody(sphereFeature, out sphere);
            hollow = Subtract(hollow, sphere);
            Face[][] mixed = Find(Body(hollow));
            Check(mixed.Length == 2 && mixed.Any(g => g.Length == 1) && mixed.Any(g => g.Length == 6),
                "Box and single-face spherical cavities");
            VerifyDelete(session, part, Body(hollow), mixed, true);
            Tag open = Subtract(Block(60, 0, 0, 20, 20, 20, s), Block(65, 5, 5, 5, 5, 20, s));
            Check(Find(Body(open)).Length == 0, "Open pocket is retained");
            Tag through = Subtract(Block(90, 0, 0, 20, 20, 20, s), Block(95, 5, -1, 5, 5, 22, s));
            Check(Find(Body(through)).Length == 0, "Through hole is retained");
            // Many distinct inner shells exercise the native tree and batch healing.
            int cavityCount = inches ? 100 : 1000;
            Tag many = Block(120, 0, 0, cavityCount + 2, 12, 12, s);
            for (int i = 0; i < cavityCount; i++)
                many = Subtract(many, Block(121 + i, 2, 2, 0.4, 2, 2, s));
            Body manyBody = Body(many);
            Face[][] groups = Find(manyBody);
            Check(groups.Length == cavityCount && groups.Sum(g => g.Length) == cavityCount * 6, "All cavity shells");
            long elapsed = MedianTime(delegate { Find(manyBody); });
            Console.WriteLine("UNITS=" + (inches ? "inch" : "mm") + " FACES=" + Body(many).GetFaces().Length +
                " SHELLS=" + groups.Length + " DETECT_MEDIAN_MS=" + elapsed);
            if (baselineDetect != null)
            {
                Face[][] old = (Face[][])baselineDetect.Invoke(baseline, new object[] { manyBody });
                Console.WriteLine("BASELINE_DETECT_MEDIAN_MS=" + MedianTime(delegate {
                    baselineDetect.Invoke(baseline, new object[] { manyBody }); }));
                Check(new HashSet<Tag>(old.SelectMany(g => g).Select(f => f.Tag)).SetEquals(
                    groups.SelectMany(g => g).Select(f => f.Tag)), "Baseline candidate parity");
            }
            VerifyDelete(session, part, Body(many), groups, false);
        }
        Console.WriteLine("PASS: cavities, sphere, open pocket, through hole, units, partial/full healing, undo; no parts saved.");
    }

    private static object MakeDialog(Assembly assembly, Type type)
    {
        object result = FormatterServices.GetUninitializedObject(type);
        object context = assembly.GetType("NXRefine.Core.NxContext", true).GetProperty("Current").GetValue(null, null);
        type.GetField("context", Flags).SetValue(result, context);
        return result;
    }

    private static Face[][] Find(Body body) { return (Face[][])detect.Invoke(dialog, new object[] { body }); }
    private static long MedianTime(Action action)
    {
        var samples = new long[5];
        for (int i = 0; i < samples.Length; i++)
        {
            var timer = Stopwatch.StartNew();
            action();
            samples[i] = timer.ElapsedMilliseconds;
        }
        Array.Sort(samples);
        return samples[2];
    }
    private static Body Body(Tag tag) { return (Body)NXObjectManager.Get(tag); }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    private static void VerifyDelete(Session session, Part part, Body body, Face[][] groups, bool partial)
    {
        var original = new HashSet<Tag>(body.GetFaces().Select(f => f.Tag));
        int bodyCount = part.Bodies.ToArray().Length;
        Face[] selected = partial ? groups[0] : groups.SelectMany(g => g).ToArray();
        var exterior = new HashSet<Tag>(original.Except(groups.SelectMany(g => g).Select(f => f.Tag)));
        var mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "Cavity regression");
        try
        {
            var timer = Stopwatch.StartNew();
            delete.Invoke(dialog, new object[] { selected });
            Console.WriteLine("DELETE_FACES=" + selected.Length + " DELETE_MS=" + timer.ElapsedMilliseconds);
            var live = new HashSet<Tag>(body.GetFaces().Select(f => f.Tag));
            Check(body.IsSolidBody && part.Bodies.ToArray().Length == bodyCount, "Solid bodies retained");
            Check(exterior.IsSubsetOf(live), "Exterior faces retained");
            Check(original.Count - live.Count == selected.Length, "Exact selected face removal");
            Check(Find(body).Length == (partial ? groups.Length - 1 : 0), "Only retained cavity groups healed");
        }
        finally
        {
            session.UndoToMark(mark, null);
            session.DeleteUndoMark(mark, null);
        }
        Check(original.SetEquals(body.GetFaces().Select(f => f.Tag)), "Undo restores face tags");
        Check(Find(body).Length == groups.Length, "Undo restores cavities");
    }

    private static Tag Block(double x, double y, double z, double a, double b, double c, double scale)
    {
        Tag feature, body;
        uf.Modl.CreateBlock1(FeatureSigns.Nullsign, new[] { x * scale, y * scale, z * scale },
            new[] { a, b, c }.Select(v => (v * scale).ToString("R", CultureInfo.InvariantCulture)).ToArray(), out feature);
        uf.Modl.AskFeatBody(feature, out body);
        return body;
    }

    private static Tag Subtract(Tag target, Tag tool)
    {
        int count;
        Tag[] bodies;
        uf.Modl.SubtractBodies(target, tool, out count, out bodies);
        if (count != 1) throw new Exception("Fixture subtraction must retain one solid");
        return bodies[0];
    }
}
