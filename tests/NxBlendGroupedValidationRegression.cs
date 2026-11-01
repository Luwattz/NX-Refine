using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using NXOpen;
using NXOpen.UF;

// Native tests operate on an input copy, undo every trial, and never save it.
internal static class NxBlendGroupedValidationRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Session session;
    private static UFSession uf;
    private static Part part;
    private static Body body;
    private static Assembly assembly;
    private static Type dialogType;
    private static Type scanType;
    private static object context;
    private static StreamWriter report;
    private static HashSet<Tag> originalFaces;
    private static int originalFaults;

    public static void Main(string[] args)
    {
        using (report = new StreamWriter(args[2]))
        {
            report.AutoFlush = true;
            try { Run(args); }
            catch (Exception ex) { report.WriteLine("FATAL=" + ex); throw; }
        }
    }

    private static void Run(string[] args)
    {
        session = Session.GetSession();
        uf = UFSession.GetUFSession();
        PartLoadStatus status;
        part = session.Parts.OpenDisplay(args[1], out status);
        status.Dispose();
        body = part.Bodies.ToArray().First(value => value.IsSolidBody && !value.IsOccurrence);
        assembly = Assembly.LoadFrom(args[0]);
        dialogType = assembly.GetType("NXRefine.UI.RemoveBlendsDialog", true);
        scanType = assembly.GetType("NXRefine.Core.SelectionScan", true);
        context = assembly.GetType("NXRefine.Core.NxContext").GetProperty("Current").GetValue(null, null);
        originalFaces = FaceTags();
        originalFaults = Faults();
        report.WriteLine("SYSLOG=" + session.LogFile.FileName);
        report.WriteLine("BASELINE_FACES=" + originalFaces.Count + " BASELINE_FAULTS=" + originalFaults);
        var whole = session.SetUndoMark(Session.MarkVisibility.Invisible, "Grouped validation regression");
        try
        {
            Tag[] sources = body.GetFaces().Where(IsSeed).OrderBy(face => face.Tag)
                .Take(int.Parse(args[4])).Select(face => face.Tag).ToArray();
            report.WriteLine("SOURCE_COUNT=" + sources.Length);
            if (sources.Length == 0) throw new Exception("No housing blend sources found.");
            Tag healable = FindHealableSource(sources);
            CheckInvalidGroupReplay(healable);
            CheckStoppedFinalization(healable, false);
            CheckStoppedFinalization(healable, true);
            int seconds = int.Parse(args[3]);
            if (seconds > 0)
            {
                Benchmark(sources, true, seconds);
                Benchmark(sources, false, seconds);
            }
            report.WriteLine("PASS: grouped validation rollback, strict replay, stopped finalization, housing final consistency, full undo.");
        }
        finally
        {
            session.UndoToMark(whole, null);
            session.DeleteUndoMark(whole, null);
            CheckRestored("FINAL");
            part.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null);
            report.WriteLine("INPUT_NOT_SAVED=True");
            try { File.Copy(session.LogFile.FileName, Path.ChangeExtension(args[2], ".syslog"), true); }
            catch (IOException) { }
        }
    }

    private static object NewDialog(Tag[] sources, bool strict)
    {
        object dialog = FormatterServices.GetUninitializedObject(dialogType);
        Set(dialog, "context", context);
        Set(dialog, "recognitionSeeds", sources);
        Set(dialog, "excludedFaces", new HashSet<Tag>());
        Set(dialog, "strictDeleteValidation", strict);
        Set(dialog, "validatedBodyFaults", new Dictionary<Tag, int> { { body.Tag, originalFaults } });
        Set(dialog, "deleteOwners", new Dictionary<Tag, Tag>());
        Set(dialog, "attemptedDeleteRegions", new HashSet<string>());
        return dialog;
    }

    private static Tag FindHealableSource(Tag[] sources)
    {
        foreach (Tag source in sources)
        {
            var mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "Find healable source");
            bool success;
            try
            {
                object dialog = NewDialog(new[] { source }, true);
                success = (bool)Call(dialog, "TryDeleteChainForMode", LookupFace(source), 3, new HashSet<string>());
            }
            finally
            {
                session.UndoToMark(mark, null);
                session.DeleteUndoMark(mark, null);
                CheckRestored("HEALABLE_PROBE");
            }
            if (success) { report.WriteLine("HEALABLE_SOURCE=" + source); return source; }
        }
        throw new Exception("No directly healable source in the bounded housing sample.");
    }

    private static object BeginGroup(object dialog, Tag source, out HashSet<Tag> seeds)
    {
        Set(dialog, "primaryJobs", Queue(source));
        Set(dialog, "retryJobs", Queue());
        seeds = new HashSet<Tag> { source };
        return Call(dialog, "BeginDeleteValidationGroup", seeds);
    }

    private static void CheckInvalidGroupReplay(Tag source)
    {
        var mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "Invalid group strict replay");
        try
        {
            object dialog = NewDialog(new[] { source }, false);
            HashSet<Tag> seeds;
            object group = BeginGroup(dialog, source, out seeds);
            if (!(bool)Call(dialog, "TryDeleteChainForMode", LookupFace(source), 3, Get(dialog, "attemptedDeleteRegions")))
                throw new Exception("Known healable source failed in tentative group.");
            // Simulate the queue work already consumed by DeleteBestEffort.
            Call(Get(dialog, "primaryJobs"), "Dequeue");
            Set(group, "Jobs", 1);
            Set(dialog, "deletePassProgress", true);
            ((Dictionary<Tag, int>)Get(dialog, "validatedBodyFaults"))[body.Tag] = -1;
            if ((bool)Call(dialog, "FinishDeleteValidationGroup", group))
                throw new Exception("Injected invalid group was accepted.");
            CheckRestored("INVALID_GROUP");
            if (QueueCount(dialog, "primaryJobs") != 1 || QueueCount(dialog, "retryJobs") != 0 ||
                ((HashSet<string>)Get(dialog, "attemptedDeleteRegions")).Count != 0 ||
                (bool)Get(dialog, "deletePassProgress") || (int)Get(dialog, "strictReplayJobs") != 1)
                throw new Exception("Invalid group did not restore the pending queue/cache/replay state.");
            if (((Dictionary<Tag, int>)Get(dialog, "validatedBodyFaults"))[body.Tag] != originalFaults)
                throw new Exception("Invalid group did not restore the validated fault baseline.");
            int removed = (int)Call(dialog, "DeleteBestEffort", new[] { source });
            if (removed < 1 || FaceTags().Contains(source)) throw new Exception("Strict replay failed to retain the valid heal.");
            CheckSafe("STRICT_REPLAY");
            report.WriteLine("PASS_INVALID_GROUP_REPLAY=True");
        }
        finally
        {
            session.UndoToMark(mark, null);
            session.DeleteUndoMark(mark, null);
            CheckRestored("REPLAY_UNDO");
        }
    }

    private static void CheckStoppedFinalization(Tag source, bool injectFailure)
    {
        var mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "Stopped group finalization");
        object scan = null;
        try
        {
            object dialog = NewDialog(new[] { source }, false);
            HashSet<Tag> seeds;
            object group = BeginGroup(dialog, source, out seeds);
            if (!(bool)Call(dialog, "TryDeleteChainForMode", LookupFace(source), 3, Get(dialog, "attemptedDeleteRegions")))
                throw new Exception("Known healable source failed before stopped finalization.");
            Call(Get(dialog, "primaryJobs"), "Dequeue");
            Set(group, "Jobs", 1);
            scan = NewScan();
            Call(scan, "RequestStop");
            if (injectFailure) ((Dictionary<Tag, int>)Get(dialog, "validatedBodyFaults"))[body.Tag] = -1;
            bool valid = (bool)Call(dialog, "FinishDeleteValidationGroup", group);
            if (valid == injectFailure) throw new Exception("Stopped group validation returned the wrong outcome.");
            if (injectFailure)
            {
                CheckRestored("STOPPED_INVALID_GROUP");
                if (QueueCount(dialog, "primaryJobs") != 1)
                    throw new Exception("Stopped invalid group lost its pending source.");
            }
            else
            {
                if (FaceTags().Contains(source)) throw new Exception("Valid stopped group did not retain the heal.");
                CheckSafe("STOPPED_VALID_GROUP");
            }
            report.WriteLine("PASS_STOPPED_FINALIZATION_INVALID=" + injectFailure);
        }
        finally
        {
            if (scan != null) ((IDisposable)scan).Dispose();
            session.UndoToMark(mark, null);
            session.DeleteUndoMark(mark, null);
            CheckRestored("STOPPED_UNDO");
        }
    }

    private static void Benchmark(Tag[] sources, bool strict, int seconds)
    {
        var mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "Housing runtime comparison");
        object scan = null;
        try
        {
            object dialog = NewDialog(sources, strict);
            Set(dialog, "continuousDelete", true);
            scan = NewScan();
            object activeScan = scan;
            report.WriteLine("BENCHMARK_START=" + (strict ? "STRICT" : "GROUPED") + " STOP_AFTER_SECONDS=" + seconds);
            var clock = Stopwatch.StartNew();
            using (var timer = new Timer(state => Call(activeScan, "RequestStop"), null, seconds * 1000, Timeout.Infinite))
            {
                try { Call(dialog, "DeleteBestEffort", sources); }
                catch (OperationCanceledException) { }
            }
            clock.Stop();
            CheckSafe(strict ? "STRICT_BENCHMARK" : "GROUPED_BENCHMARK");
            HashSet<Tag> current = FaceTags();
            int remaining = current.Count;
            int checks = (int)Get(dialog, "consistencyChecks");
            report.WriteLine("BENCHMARK=" + (strict ? "STRICT" : "GROUPED") + " WALL_MS=" + clock.ElapsedMilliseconds +
                " ATTEMPTS=" + Get(dialog, "deleteAttempts") + " CHECKS=" + checks +
                " REMOVED_TOTAL=" + (originalFaces.Count - remaining) + " REMOVED_SOURCES=" + sources.Count(tag => !current.Contains(tag)));
        }
        finally
        {
            if (scan != null) ((IDisposable)scan).Dispose();
            session.UndoToMark(mark, null);
            session.DeleteUndoMark(mark, null);
            CheckRestored("BENCHMARK_UNDO");
        }
    }

    private static object Queue(params Tag[] tags)
    {
        Type jobType = dialogType.GetNestedType("DeleteJob", BindingFlags.NonPublic);
        object queue = Activator.CreateInstance(typeof(Queue<>).MakeGenericType(jobType));
        foreach (Tag tag in tags)
        {
            object job = Activator.CreateInstance(jobType, true);
            Set(job, "Seed", tag); Set(job, "Mode", 3);
            Call(queue, "Enqueue", job);
        }
        return queue;
    }

    private static int QueueCount(object dialog, string name) { return (int)Get(dialog, name).GetType().GetProperty("Count").GetValue(Get(dialog, name), null); }
    private static object NewScan() { return Activator.CreateInstance(scanType, Private, null, new object[] { context, null }, null); }
    private static object Get(object target, string name) { return target.GetType().GetField(name, Private).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, Private).SetValue(target, value); }
    private static object Call(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name, Private | BindingFlags.Public).Invoke(target, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static Face LookupFace(Tag tag) { return (Face)NXOpen.Utilities.NXObjectManager.Get(tag); }
    private static HashSet<Tag> FaceTags() { return new HashSet<Tag>(part.Bodies.ToArray().SelectMany(value => value.GetFaces()).Select(face => face.Tag)); }
    private static int Faults() { int count; int[] faults; Tag[] objects; uf.Modl.AskBodyConsistency(body.Tag, out count, out faults, out objects); return count; }
    private static void CheckSafe(string label)
    {
        int faults = Faults();
        if (part.Bodies.ToArray().Any(value => !value.IsSolidBody) || faults > originalFaults)
            throw new Exception(label + " retained an unsafe result: faults=" + faults);
        report.WriteLine(label + "_FAULTS=" + faults);
    }
    private static void CheckRestored(string label)
    {
        if (!originalFaces.SetEquals(FaceTags()) || part.Bodies.ToArray().Any(value => !value.IsSolidBody))
            throw new Exception(label + " did not restore original solid face tags.");
        report.WriteLine(label + "_UNDO_RESTORED=True");
    }
    private static bool IsSeed(Face face)
    {
        double radius; bool blend;
        try { face.GetBlendData(out radius, out blend); if (blend && radius > 0) return radius <= 3; }
        catch (NXException) { }
        try
        {
            int type, normal; double extra;
            uf.Modl.AskFaceData(face.Tag, out type, new double[3], new double[3], new double[6], out radius, out extra, out normal);
            return (type == 23 || face.SolidFaceType == Face.FaceType.Blending) && radius > 0 && radius <= 3;
        }
        catch (NXException) { return false; }
    }
}
