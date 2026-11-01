using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;

// Read-only source fixture; every complete native-rule trial is individually undone.
internal static class NxBlendNarrowProbe
{
    private static Session session;
    private static UFSession uf;
    private static Part part;
    private static StreamWriter report;

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
        part = session.Parts.OpenDisplay(Path.GetFullPath(args[0]), out load);
        load.Dispose();
        report.WriteLine("SYSLOG=" + session.LogFile.FileName);
        int maximumRegions = int.Parse(args[2]), maximumTrials = int.Parse(args[3]);
        int seconds = int.Parse(args[4]);
        bool requirePrimaryFailure = args.Length > 5 && bool.Parse(args[5]);
        var original = new HashSet<Tag>(Faces().Select(face => face.Tag));
        int bodyCount = part.Bodies.ToArray().Length;
        var outer = session.SetUndoMark(Session.MarkVisibility.Invisible, "Narrow native blend probe");
        var timer = Stopwatch.StartNew();
        int regions = 0, sources = 0, opportunities = 0, trials = 0, healed = 0, removed = 0;
        int primaryTrials = 0, primaryHealed = 0;
        long queryTime = 0, trialTime = 0;
        try
        {
            Tag[] seeds = Faces().Where(Eligible).Select(face => face.Tag).OrderBy(tag => tag).ToArray();
            report.WriteLine("SEEDS=" + seeds.Length + " FILTER_MS=" + timer.ElapsedMilliseconds);
            var seen = new HashSet<string>(); var narrowSeen = new HashSet<string>();
            var primaryResults = new Dictionary<Tag, bool>();
            foreach (Tag seed in seeds)
            {
                if (regions >= maximumRegions || timer.Elapsed.TotalSeconds >= seconds) break;
                sources++;
                var watch = Stopwatch.StartNew();
                HashSet<Tag> broad = Region(seed, 3);
                queryTime += watch.ElapsedMilliseconds;
                if (broad.Count == 0 || !seen.Add(Key(broad))) continue;
                regions++;
                report.WriteLine("REGION=" + regions + " SEED=" + seed + " MODE3=" + broad.Count);
                foreach (int mode in new[] { 1, 2, 0 })
                {
                    if (timer.Elapsed.TotalSeconds >= seconds) break;
                    watch.Restart();
                    HashSet<Tag> narrow = Region(seed, mode);
                    queryTime += watch.ElapsedMilliseconds;
                    bool subset = narrow.Count > 0 && broad.IsProperSupersetOf(narrow);
                    bool affordable = subset && narrow.Count <= 8;
                    report.WriteLine("CANDIDATE SEED=" + seed + " MODE=" + mode + " FACES=" + narrow.Count +
                        " STRICT_SUBSET=" + subset + " QUERY_MS=" + watch.ElapsedMilliseconds);
                    if (!affordable || !narrowSeen.Add(Key(narrow))) continue;
                    opportunities++;
                    if (trials >= maximumTrials) continue;
                    if (requirePrimaryFailure)
                    {
                        bool primarySuccess;
                        if (!primaryResults.TryGetValue(seed, out primarySuccess))
                        {
                            if (broad.Count > 16) { report.WriteLine("PRIMARY_TOO_LARGE_SKIP SEED=" + seed); continue; }
                            primaryTrials++;
                            watch.Restart();
                            int primaryRemoved;
                            primarySuccess = Trial(seed, 3, broad, broad, out primaryRemoved);
                            trialTime += watch.ElapsedMilliseconds;
                            primaryResults.Add(seed, primarySuccess);
                            if (primarySuccess) primaryHealed++;
                        }
                        if (primarySuccess) { report.WriteLine("PRIMARY_HEALS_SKIP_SUBSET SEED=" + seed); continue; }
                        report.WriteLine("PRIMARY_FAILED_TRY_SUBSET SEED=" + seed + " MODE=" + mode);
                    }
                    trials++;
                    watch.Restart();
                    int count;
                    bool success = Trial(seed, mode, broad, narrow, out count);
                    trialTime += watch.ElapsedMilliseconds;
                    if (success) { healed++; removed += count; }
                }
            }
            report.WriteLine("SUMMARY REGIONS=" + regions + " SOURCES=" + sources + " UNIQUE_SMALL_SUBSETS=" + opportunities +
                " TRIALS=" + trials + " HEALED=" + healed + " SELECTED_FACES_REMOVED=" + removed +
                " REQUIRE_PRIMARY_FAILURE=" + requirePrimaryFailure + " PRIMARY_TRIALS=" + primaryTrials + " PRIMARY_HEALED=" + primaryHealed +
                " QUERY_MS=" + queryTime + " TRIAL_MS=" + trialTime + " TOTAL_MS=" + timer.ElapsedMilliseconds);
        }
        finally
        {
            session.UndoToMark(outer, null); session.DeleteUndoMark(outer, null);
            bool restored = original.SetEquals(Faces().Select(face => face.Tag)) && part.Bodies.ToArray().Length == bodyCount;
            report.WriteLine("UNDO_RESTORED=" + restored);
            part.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null);
            report.WriteLine("INPUT_NOT_SAVED=True");
            try { File.Copy(session.LogFile.FileName, Path.ChangeExtension(args[1], ".syslog"), true); }
            catch (IOException) { }
            if (!restored) throw new Exception("Outer undo failed.");
        }
    }

    private static Face[] Faces()
    { return part.Bodies.ToArray().Where(body => body.IsSolidBody && !body.IsOccurrence).SelectMany(body => body.GetFaces()).ToArray(); }
    private static string Key(IEnumerable<Tag> tags) { return string.Join(",", tags.OrderBy(tag => tag)); }
    private static Face Seed(Tag tag) { return (Face)NXOpen.Utilities.NXObjectManager.Get(tag); }
    private static bool Eligible(Face face)
    {
        double radius; bool blend;
        try { face.GetBlendData(out radius, out blend); if (blend && radius > 0) return radius <= 3; }
        catch (NXException) { }
        try
        {
            int type, direction; double other;
            uf.Modl.AskFaceData(face.Tag, out type, new double[3], new double[3], new double[6], out radius, out other, out direction);
            return (type == 23 || face.SolidFaceType == Face.FaceType.Blending) && radius > 0 && radius <= 3;
        }
        catch (NXException) { return false; }
    }
    private static FaceConnectedBlendRule Rule(Tag tag, int mode)
    { return part.ScRuleFactory.CreateRuleFaceConnectedBlend(Seed(tag), (mode & 1) != 0, (mode & 2) != 0, null); }
    private static HashSet<Tag> Region(Tag seed, int mode)
    {
        var collector = part.ScCollectors.CreateCollector(); FaceConnectedBlendRule rule = null;
        try
        {
            rule = Rule(seed, mode); collector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
            return new HashSet<Tag>(collector.GetObjects().OfType<Face>().Select(face => face.Tag));
        }
        finally { try { collector.Destroy(); } finally { if (rule != null) rule.Dispose(); } }
    }
    private static int Faults(Body body)
    { int count; int[] faults; Tag[] objects; uf.Modl.AskBodyConsistency(body.Tag, out count, out faults, out objects); return count; }
    private static bool Trial(Tag seed, int mode, HashSet<Tag> broad, HashSet<Tag> expected, out int removed)
    {
        removed = 0;
        var mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "Complete narrow native rule trial");
        DeleteFaceBuilder builder = null; FaceConnectedBlendRule rule = null;
        Body body = Seed(seed).GetBody(); var original = new HashSet<Tag>(Faces().Select(face => face.Tag));
        int faults = Faults(body); var watch = Stopwatch.StartNew();
        try
        {
            builder = part.Features.CreateDeleteFaceBuilder(null); builder.Type = DeleteFaceBuilder.SelectTypes.Face; builder.Heal = true;
            rule = Rule(seed, mode); builder.FaceCollector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
            Face[] chain = builder.FaceCollector.GetObjects().OfType<Face>().ToArray();
            var selected = new HashSet<Tag>(chain.Select(face => face.Tag));
            if (!selected.SetEquals(expected) || (mode != 3 && (!broad.IsProperSupersetOf(selected) || selected.Count > 8)) || selected.Count == 0 ||
                chain.Any(face => face.GetBody().Tag != body.Tag)) throw new Exception("Live complete rule changed; trial rejected.");
            report.WriteLine("TRIAL_START SEED=" + seed + " MODE=" + mode + " FACES=" + selected.Count);
            builder.CommitFeature();
            var live = new HashSet<Tag>(Faces().Select(face => face.Tag));
            removed = selected.Count(tag => !live.Contains(tag));
            int finalFaults = Faults(body);
            if (removed != selected.Count || !body.IsSolidBody || finalFaults > faults) throw new Exception("Native removal/solid/consistency check failed.");
            report.WriteLine("TRIAL_PASS SEED=" + seed + " MODE=" + mode + " REMOVED=" + removed +
                " EXTRA_REMOVED=" + original.Count(tag => !selected.Contains(tag) && !live.Contains(tag)) +
                " FAULTS_BEFORE=" + faults + " FAULTS_AFTER=" + finalFaults + " MS=" + watch.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            report.WriteLine("TRIAL_FAIL SEED=" + seed + " MODE=" + mode + " MS=" + watch.ElapsedMilliseconds + " ERROR=" + ex.Message);
            return false;
        }
        finally
        {
            try { if (builder != null) builder.Destroy(); }
            finally
            {
                try { if (rule != null) rule.Dispose(); }
                finally
                {
                    session.UndoToMark(mark, null); session.DeleteUndoMark(mark, null);
                    if (!original.SetEquals(Faces().Select(face => face.Tag))) throw new Exception("Individual trial undo failed.");
                }
            }
        }
    }
}
