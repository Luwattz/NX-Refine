using System;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;

public class RunNxBlendCollectorRegression
{
    public static void Main(string[] args)
    {
        Session session = Session.GetSession();
        UFSession uf = UFSession.GetUFSession();
        Part part = session.Parts.NewDisplay(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "blend_collector_" + Guid.NewGuid().ToString("N") + ".prt"), Part.Units.Millimeters);
        Tag block, bodyTag, blend;
        uf.Modl.CreateBlock1(FeatureSigns.Nullsign, new double[3], new[] { "10", "10", "10" }, out block);
        uf.Modl.AskFeatBody(block, out bodyTag);
        Tag[] edges;
        uf.Modl.AskBodyEdges(bodyTag, out edges);
        uf.Modl.CreateBlend("1", new[] { edges[0] }, 0, 0, 0, 0.001, out blend);
        Body body = (Body)NXOpen.Utilities.NXObjectManager.Get(bodyTag);
        List<Face> blends = new List<Face>();
        foreach (Face face in body.GetFaces())
        {
            double radius; bool isBlend;
            face.GetBlendData(out radius, out isBlend);
            if (isBlend) blends.Add(face);
        }
        if (blends.Count != 1) throw new Exception("Expected one synthetic blend face.");
        Tag selected = blends[0].Tag;
        Assembly assembly = Assembly.LoadFrom(args[0]);
        Type type = assembly.GetType("NXRefine.UI.RemoveBlendsDialog", true);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object dialog = FormatterServices.GetUninitializedObject(type);
        type.GetField("context", flags).SetValue(dialog,
            assembly.GetType("NXRefine.Core.NxContext").GetProperty("Current").GetValue(null, null));
        FaceConnectedBlendRule rule = (FaceConnectedBlendRule)type.GetMethod("CreateConnectedBlendRule", flags)
            .Invoke(dialog, new object[] { blends[0] });
        Face ruleSeed;
        bool blendLike, unlabeled;
        Feature feature;
        rule.GetDefiningData(out ruleSeed, out blendLike, out unlabeled, out feature);
        if (!blendLike || !unlabeled || ruleSeed.Tag != selected)
            throw new Exception("Complete loop recognition must include blend-like and unlabeled faces.");
        // Recognition has already supplied the seed; the delete phase must not
        // reject its complete rule result using the seed radius limit again.
        type.GetField("activeMaxRadius", flags).SetValue(dialog, 0.01);
        FieldInfo excluded = type.GetField("excludedFaces", flags);
        excluded.SetValue(dialog, Activator.CreateInstance(excluded.FieldType, new object[] { new[] { selected } }));
        MethodInfo remove = type.GetMethod("TryDeleteConnectedChain", flags);
        if ((bool)remove.Invoke(dialog, new object[] { blends[0] }) || body.GetFaces().Length != 7)
            throw new Exception("A manually deselected face was deleted.");
        excluded.SetValue(dialog, Activator.CreateInstance(excluded.FieldType));
        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "collector regression");
        bool success = (bool)remove.Invoke(dialog, new object[] { blends[0] });
        if (!success || body.GetFaces().Length != 6 || !body.IsSolidBody)
            throw new Exception("Native connected-face rule must restore a six-face solid block.");
        foreach (Face face in body.GetFaces()) if (face.Tag == selected) throw new Exception("Blend face still present.");
        session.UndoToMark(mark, null);
        session.DeleteUndoMark(mark, null);
        if (body.GetFaces().Length != 7) throw new Exception("Undo must restore the rounded block.");
        MethodInfo removeMode = type.GetMethod("TryDeleteChainForMode", flags);
        foreach (int mode in new[] { 0, 1, 2 })
        {
            Face current = (Face)NXOpen.Utilities.NXObjectManager.Get(selected);
            excluded.SetValue(dialog, Activator.CreateInstance(excluded.FieldType, new object[] { new[] { selected } }));
            if ((bool)removeMode.Invoke(dialog, new object[] { current, mode, null }))
                throw new Exception("Fallback recognition ignored manual exclusion.");
            excluded.SetValue(dialog, Activator.CreateInstance(excluded.FieldType));
            mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "fallback collector regression");
            try
            {
                if (!(bool)removeMode.Invoke(dialog, new object[] { current, mode, null }) ||
                    body.GetFaces().Length != 6 || !body.IsSolidBody)
                    throw new Exception("Fallback native rule failed for mode " + mode);
            }
            finally
            {
                session.UndoToMark(mark, null);
                session.DeleteUndoMark(mark, null);
            }
            if (body.GetFaces().Length != 7) throw new Exception("Fallback undo failed.");
        }
        // An exhausted delete budget must stop before another native rule,
        // while a subsequent preview still returns the complete selection.
        var clock = Stopwatch.StartNew();
        type.GetField("deleteClock", flags).SetValue(dialog, clock);
        type.GetField("deleteAttempts", flags).SetValue(dialog, 256);
        type.GetField("continuousDelete", flags).SetValue(dialog, true);
        if (!(bool)type.GetMethod("DeleteBudgetAvailable", flags).Invoke(dialog, null))
            throw new Exception("Interactive continuous cleanup must not stop at the diagnostic budget.");
        type.GetField("continuousDelete", flags).SetValue(dialog, false);
        Face restored = (Face)NXOpen.Utilities.NXObjectManager.Get(selected);
        var enumerable = (IEnumerable)type.GetMethod("EnumerateChainsForMode", flags)
            .Invoke(dialog, new object[] { new[] { restored }, 3, true });
        var enumerator = enumerable.GetEnumerator();
        try { if (enumerator.MoveNext()) throw new Exception("Recognition exceeded the delete attempt budget."); }
        finally { ((IDisposable)enumerator).Dispose(); }
        var preview = (Face[][])type.GetMethod("BuildFastPreviewGroups", flags)
            .Invoke(dialog, new object[] { new[] { restored } });
        if (preview.Length != 1 || preview[0].Length != 1 || preview[0][0].Tag != selected)
            throw new Exception("An exhausted previous deletion budget truncated the preview.");
        clock.Stop();
        type.GetField("recognitionSeeds", flags).SetValue(dialog, new[] { selected });
        excluded.SetValue(dialog, Activator.CreateInstance(excluded.FieldType, new object[] { new[] { selected } }));
        var bestEffort = type.GetMethod("DeleteBestEffort", flags);
        if ((int)bestEffort.Invoke(dialog, new object[] { new[] { selected } }) != 0 || body.GetFaces().Length != 7)
            throw new Exception("Streamed deletion ignored manual exclusion.");
        excluded.SetValue(dialog, Activator.CreateInstance(excluded.FieldType));
        mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "streamed delete regression");
        try
        {
            if ((int)bestEffort.Invoke(dialog, new object[] { new[] { selected } }) != 1 ||
                body.GetFaces().Length != 6 || !body.IsSolidBody)
                throw new Exception("Streamed native deletion failed to remove the synthetic blend.");
        }
        finally { session.UndoToMark(mark, null); session.DeleteUndoMark(mark, null); }
        if (body.GetFaces().Length != 7) throw new Exception("Streamed deletion undo failed.");
        // A stop leaves the unstarted job queued. Resume must use the same
        // reviewed selection, including faces manually excluded by the user.
        object context = assembly.GetType("NXRefine.Core.NxContext").GetProperty("Current").GetValue(null, null);
        Type scanType = assembly.GetType("NXRefine.Core.SelectionScan", true);
        object scan = Activator.CreateInstance(scanType, flags, null, new object[] { context, null }, null);
        try
        {
            scanType.GetMethod("ReportProgress", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { "Progress regression", 42, 100 });
            if ((int)scanType.GetField("progressCompleted", flags).GetValue(scan) != 42 ||
                (int)scanType.GetField("progressTotal", flags).GetValue(scan) != 100)
                throw new Exception("Numeric scan progress was not published to the active window state.");
            type.GetField("continuousDelete", flags).SetValue(dialog, true);
            Tag extraSource = Array.Find(body.GetFaces(), face => face.Tag != selected).Tag;
            type.GetField("recognitionSeeds", flags).SetValue(dialog, new[] { extraSource, selected });
            scanType.GetMethod("RequestStop", flags).Invoke(scan, null);
            bool canceled = false;
            try { bestEffort.Invoke(dialog, new object[] { new[] { selected } }); }
            catch (TargetInvocationException ex) { canceled = ex.InnerException is OperationCanceledException; }
            if (!canceled || body.GetFaces().Length != 7)
                throw new Exception("Stopped deletion must preserve geometry and report cancellation.");
            var pending = new List<Tag>();
            foreach (object job in (IEnumerable)type.GetField("primaryJobs", flags).GetValue(dialog))
                pending.Add((Tag)job.GetType().GetField("Seed", flags).GetValue(job));
            if (pending.Count != 2 || !pending.Contains(selected) || !pending.Contains(extraSource))
                throw new Exception("Continuous cleanup must retain every original source in the pending queue.");
            type.GetField("recognitionSeeds", flags).SetValue(dialog, new[] { selected });
            type.GetField("primaryJobs", flags).SetValue(dialog, null);
            type.GetField("retryJobs", flags).SetValue(dialog, null);
            try { bestEffort.Invoke(dialog, new object[] { new[] { selected } }); }
            catch (TargetInvocationException ex) { if (!(ex.InnerException is OperationCanceledException)) throw; }
            var stoppedRegions = new HashSet<string>();
            canceled = false;
            try { removeMode.Invoke(dialog, new object[] {
                (Face)NXOpen.Utilities.NXObjectManager.Get(selected), 3, stoppedRegions }); }
            catch (TargetInvocationException ex) { canceled = ex.InnerException is OperationCanceledException; }
            if (!canceled || stoppedRegions.Count != 0 || body.GetFaces().Length != 7)
                throw new Exception("Stop before commit must not cache an unattempted chain as a failure.");
        }
        finally { ((IDisposable)scan).Dispose(); type.GetField("continuousDelete", flags).SetValue(dialog, false); }
        foreach (string name in new[] { "bodies", "candidates", "retained" })
        {
            FieldInfo field = type.GetField(name, flags);
            field.SetValue(dialog, Activator.CreateInstance(field.FieldType));
        }
        Tag excludedTag = Array.Find(body.GetFaces(), face => face.Tag != selected).Tag;
        type.GetMethod("RestorePendingBlends", flags).Invoke(dialog,
            new object[] { new[] { body.Tag }, new[] { selected, excludedTag }, new[] { selected } });
        var retainedTags = (System.Collections.Generic.HashSet<Tag>)type.GetField("retained", flags).GetValue(dialog);
        if (!retainedTags.SetEquals(new[] { selected }))
            throw new Exception("Resume changed the manually reviewed selection.");
        mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "stopped delete resume regression");
        try
        {
            if ((int)bestEffort.Invoke(dialog, new object[] { new[] { selected } }) != 1 || body.GetFaces().Length != 6)
                throw new Exception("Stopped native deletion did not resume.");
        }
        finally { session.UndoToMark(mark, null); session.DeleteUndoMark(mark, null); }
        Console.WriteLine("PASS: all four live native recognition modes, weak radius, manual exclusion, streamed deletion, recognition budget, complete preview after budget, solid body, and undo. No part saved.");
        Console.WriteLine("PASS: stop before native commit, reviewed selection restoration, pending queue resume; no part saved.");
        Console.WriteLine("PASS: numeric progress, continuous cleanup beyond diagnostic budget, Stop still honored.");
    }
}

