using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;

// Isolated native batching probe. Each single/pair is undone to the same input.
internal static class NxBlendBatchProbe
{
    private sealed class Group { public Tag Seed; public Tag[] Tags; }
    private sealed class Trial { public bool Success; public long Commit; public string Error; }
    private static Session session;
    private static UFSession uf;
    private static Part part;
    private static StreamWriter report;
    private static Tag bodyTag;
    private static int baselineFaults;
    private static HashSet<Tag> baselineTags;

    public static void Main(string[] args)
    {
        using (report = new StreamWriter(args[1]))
        {
            report.AutoFlush = true;
            try { Run(args); }
            catch (Exception ex) { report.WriteLine("FATAL=" + ex); throw; }
        }
    }

    private static void Run(string[] args)
    {
        session = Session.GetSession(); uf = UFSession.GetUFSession();
        PartLoadStatus load;
        part = session.Parts.OpenDisplay(args[0], out load); load.Dispose();
        report.WriteLine("SYSLOG=" + session.LogFile.FileName);
        Body body = part.Bodies.ToArray().First(b => b.IsSolidBody && !b.IsOccurrence);
        bodyTag = body.Tag;
        var setupClock = Stopwatch.StartNew();
        baselineTags = new HashSet<Tag>(Faces().Select(face => face.Tag));
        long baselineEnumeration = setupClock.ElapsedMilliseconds; setupClock.Restart();
        baselineFaults = Faults();
        report.WriteLine("BASELINE_ENUM_MS=" + baselineEnumeration + " BASELINE_CONSISTENCY_MS=" + setupClock.ElapsedMilliseconds);
        var whole = session.SetUndoMark(Session.MarkVisibility.Invisible, "Native blend batch probe");
        var timer = Stopwatch.StartNew();
        try
        {
            Face[] seeds = body.GetFaces().Where(IsSeed).OrderBy(face => face.Tag).ToArray();
            report.WriteLine("SEEDS=" + seeds.Length + " BASELINE_FACES=" + baselineTags.Count + " FAULTS=" + baselineFaults);
            var groups = new List<Group>(); var occupied = new HashSet<Tag>(); var keys = new HashSet<string>();
            foreach (Face seed in seeds)
            {
                if (groups.Count >= int.Parse(args[2])) break;
                if (timer.Elapsed.TotalSeconds >= int.Parse(args[3])) break;
                ScCollector collector = part.ScCollectors.CreateCollector();
                FaceConnectedBlendRule rule = null;
                try
                {
                    rule = Rule(seed);
                    collector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                    Face[] faces = collector.GetObjects().OfType<Face>().ToArray();
                    if (faces.Length == 0 || faces.Any(face => face.GetBody().Tag != bodyTag)) continue;
                    Tag[] tags = faces.Select(face => face.Tag).Distinct().OrderBy(tag => tag).ToArray();
                    if (!keys.Add(string.Join(",", tags)) || tags.Any(occupied.Contains)) continue;
                    groups.Add(new Group { Seed = seed.Tag, Tags = tags }); occupied.UnionWith(tags);
                    report.WriteLine("GROUP=" + (groups.Count - 1) + " SEED=" + seed.Tag + " FACES=" + tags.Length + " TAGS=" + string.Join(",", tags));
                }
                catch (NXException ex) { report.WriteLine("RECOGNITION_FAILED=" + seed.Tag + " " + ex.Message); }
                finally { try { collector.Destroy(); } finally { if (rule != null) rule.Dispose(); } }
            }
            report.WriteLine("DISJOINT_GROUPS=" + groups.Count + " PREP_MS=" + timer.ElapsedMilliseconds);
            for (int i = 0; i + 1 < groups.Count; i += 2)
            {
                if (timer.Elapsed.TotalSeconds >= int.Parse(args[3])) { report.WriteLine("PROBE_BUDGET=True"); break; }
                Trial a = Try(new[] { groups[i] }, "SINGLE_" + i);
                Trial b = Try(new[] { groups[i + 1] }, "SINGLE_" + (i + 1));
                Trial pair = Try(new[] { groups[i], groups[i + 1] }, "PAIR_" + i + "_" + (i + 1));
                report.WriteLine("PAIR_SUMMARY=" + i + "," + (i + 1) + " SINGLES_SUCCESS=" + a.Success + "," + b.Success +
                    " PAIR_SUCCESS=" + pair.Success + " SINGLES_COMMIT_MS=" + (a.Commit + b.Commit) + " PAIR_COMMIT_MS=" + pair.Commit +
                    " RESCUES_FAILURE=" + (pair.Success && (!a.Success || !b.Success)));
            }
        }
        finally
        {
            session.UndoToMark(whole, null); session.DeleteUndoMark(whole, null);
            bool restored = baselineTags.SetEquals(Faces().Select(face => face.Tag)) && part.Bodies.ToArray().All(b => b.IsSolidBody);
            report.WriteLine("UNDO_RESTORED=" + restored);
            part.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null);
            report.WriteLine("INPUT_NOT_SAVED=True");
            try { File.Copy(session.LogFile.FileName, Path.ChangeExtension(args[1], ".syslog"), true); } catch (IOException) { }
            if (!restored) throw new Exception("Probe did not restore the original solid/tag set.");
        }
    }

    private static Trial Try(Group[] groups, string label)
    {
        var result = new Trial();
        var mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "Native batch trial");
        DeleteFaceBuilder builder = null;
        var rules = new List<FaceConnectedBlendRule>();
        var clock = Stopwatch.StartNew();
        long recognize = 0, check = 0, rollback = 0;
        try
        {
            Dictionary<Tag, Face> live = Faces().ToDictionary(face => face.Tag);
            builder = part.Features.CreateDeleteFaceBuilder(null); builder.Type = DeleteFaceBuilder.SelectTypes.Face; builder.Heal = true;
            var union = new HashSet<Tag>();
            foreach (Group group in groups)
            {
                Face seed;
                if (!live.TryGetValue(group.Seed, out seed)) throw new Exception("Original source is absent.");
                FaceConnectedBlendRule rule = Rule(seed); rules.Add(rule);
                builder.FaceCollector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                Face[] faces = builder.FaceCollector.GetObjects().OfType<Face>().ToArray();
                var tags = new HashSet<Tag>(faces.Select(face => face.Tag));
                if (!tags.SetEquals(group.Tags)) throw new Exception("Native region changed on restored original geometry.");
                if (faces.Any(face => face.GetBody().Tag != bodyTag) || tags.Overlaps(union)) throw new Exception("Cross-body or overlapping rules.");
                union.UnionWith(tags);
            }
            var combined = new HashSet<Tag>(union);
            if (rules.Count > 1)
            {
                builder.FaceCollector.ReplaceRules(rules.Cast<SelectionIntentRule>().ToArray(), false);
                combined = new HashSet<Tag>(builder.FaceCollector.GetObjects().OfType<Face>().Select(face => face.Tag));
            }
            if (!combined.SetEquals(union)) throw new Exception("Combined native collector differs from disjoint union.");
            recognize = clock.ElapsedMilliseconds;
            report.WriteLine("START=" + label + " CHAINS=" + groups.Length + " FACES=" + combined.Count + " RECOGNITION_MS=" + recognize);
            clock.Restart();
            try { builder.CommitFeature(); } finally { result.Commit = clock.ElapsedMilliseconds; }
            clock.Restart();
            var current = new HashSet<Tag>(Faces().Select(face => face.Tag));
            long enumerateMs = clock.ElapsedMilliseconds;
            int faults = Faults();
            long faultMs = clock.ElapsedMilliseconds - enumerateMs;
            int removed = combined.Count(tag => !current.Contains(tag));
            int extras = baselineTags.Count(tag => !current.Contains(tag) && !combined.Contains(tag));
            bool solid = part.Bodies.ToArray().All(body => body.IsSolidBody);
            check = clock.ElapsedMilliseconds;
            if (removed != combined.Count || !solid || faults > baselineFaults) throw new Exception("Partial removal, non-solid or increased consistency faults.");
            result.Success = true;
            report.WriteLine("SUCCESS=" + label + " REMOVED=" + removed + " EXTRA_REMOVED=" + extras + " FAULTS=" + faults +
                " ENUM_MS=" + enumerateMs + " CONSISTENCY_MS=" + faultMs);
        }
        catch (Exception ex) { result.Error = ex.Message; report.WriteLine("FAILED=" + label + " ERROR=" + ex.Message); }
        finally
        {
            clock.Restart();
            try { if (builder != null) builder.Destroy(); }
            finally
            {
                foreach (FaceConnectedBlendRule rule in rules) try { rule.Dispose(); } catch (NXException) { }
                session.UndoToMark(mark, null); session.DeleteUndoMark(mark, null);
            }
            rollback = clock.ElapsedMilliseconds;
            if (!baselineTags.SetEquals(Faces().Select(face => face.Tag))) throw new Exception("Trial undo failed: " + label);
            report.WriteLine("TIMING=" + label + " SUCCESS=" + result.Success + " RECOGNITION_MS=" + recognize + " COMMIT_MS=" + result.Commit + " CHECK_MS=" + check + " ROLLBACK_MS=" + rollback);
        }
        return result;
    }

    private static Face[] Faces() { return part.Bodies.ToArray().Where(body => body.IsSolidBody).SelectMany(body => body.GetFaces()).ToArray(); }
    private static FaceConnectedBlendRule Rule(Face seed) { return part.ScRuleFactory.CreateRuleFaceConnectedBlend(seed, true, true, null); }
    private static int Faults() { int count; int[] faults; Tag[] objects; uf.Modl.AskBodyConsistency(bodyTag, out count, out faults, out objects); return count; }
    private static bool IsSeed(Face face)
    {
        double radius = 0; bool blend = false;
        try { face.GetBlendData(out radius, out blend); if (blend && radius > 0) return radius <= 3; } catch (NXException) { }
        try
        {
            int type, normal; double extra;
            uf.Modl.AskFaceData(face.Tag, out type, new double[3], new double[3], new double[6], out radius, out extra, out normal);
            return (type == 23 || face.SolidFaceType == Face.FaceType.Blending) && radius > 0 && radius <= 3;
        }
        catch (NXException) { return false; }
    }
}
