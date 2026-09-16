using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;

// Isolated NX process only. Opens a disposable fixture copy, invokes the
// production scanner, tests its Delete Face settings, undoes, and never saves.
public static class NxMarkingsModelRegression
{
    public static void Main(string[] args)
    {
        using (var report = new StreamWriter(args[2], false))
        {
            try { Run(args, report); }
            catch (Exception ex) { report.WriteLine(ex.ToString()); throw; }
        }
    }

    private static void Run(string[] args, TextWriter report)
    {
        Session session = Session.GetSession();
        UFSession uf = UFSession.GetUFSession();
        PartLoadStatus status;
        Part part = session.Parts.OpenDisplay(args[0], out status);
        status.Dispose();
        Assembly assembly = Assembly.LoadFrom(args[1]);
        report.WriteLine("ASSEMBLY=" + assembly.Location);
        report.WriteLine("MVID=" + assembly.ManifestModule.ModuleVersionId);
        Type type = assembly.GetType("NXRefine.UI.MarkingsDialog", true);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object dialog = FormatterServices.GetUninitializedObject(type);
        type.GetField("context", flags).SetValue(dialog,
            assembly.GetType("NXRefine.Core.NxContext").GetProperty("Current").GetValue(null, null));
        Body body = part.Bodies.ToArray().Single();
        Face carrier = body.GetFaces().OrderByDescending(face => face.GetEdges().Length).First();
        var carriers = new Dictionary<Tag, Face> { { carrier.Tag, carrier } };
        var groups = new List<Face[]>();
        type.GetField("carriers", flags).SetValue(dialog, carriers);
        type.GetField("groups", flags).SetValue(dialog, groups);
        int faceCount = body.GetFaces().Length;
        if (faceCount != 13 || carrier.GetEdges().Length != 10)
            throw new Exception("Expected the model2 13-face fixture with a 10-edge carrier.");

        foreach (double limit in new[] { 0.1, 0.2, 2.0, 50.0 })
        {
            groups.Clear();
            var heights = new List<double>();
            object[] scanArgs = { body, new[] { carrier }, limit, heights, 0 };
            type.GetMethod("ScanBody", flags).Invoke(dialog, scanArgs);
            int count = groups.Sum(group => group.Length);
            report.WriteLine("LIMIT=" + limit + " GROUPS=" + groups.Count + " FACES=" + count +
                " HEIGHTS=" + string.Join(",", heights.Select(h => h.ToString("R")).ToArray()));
            int expected = limit < 0.2 ? 0 : 7;
            if (count != expected) throw new Exception("Expected " + expected + " marking faces.");
            if (heights.Any(h => Math.Abs(h - 0.2) > 1e-6)) throw new Exception("Incorrect marking height.");
            if (groups.SelectMany(group => group).Any(face => face.Tag == carrier.Tag))
                throw new Exception("Carrier selected for deletion.");
        }

        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "Markings regression");
        try
        {
            DeleteFaceBuilder builder = part.Features.CreateDeleteFaceBuilder(null);
            try
            {
                builder.Type = DeleteFaceBuilder.SelectTypes.Face;
                builder.Heal = true;
                builder.FaceCollector.ReplaceRules(new SelectionIntentRule[] {
                    part.ScRuleFactory.CreateRuleFaceDumb(groups.SelectMany(group => group).ToArray()) }, false);
                builder.CommitFeature();
            }
            finally { builder.Destroy(); }
            Body[] result = part.Bodies.ToArray();
            if (result.Length != 1 || !result[0].IsSolidBody || result[0].GetFaces().Length != 6)
                throw new Exception("Healing did not produce one six-face solid.");
            int faults;
            int[] codes;
            Tag[] objects;
            uf.Modl.AskBodyConsistency(result[0].Tag, out faults, out codes, out objects);
            if (faults != 0) throw new Exception("Body consistency faults=" + faults);
            report.WriteLine("HEAL=PASS SOLIDS=1 FACES=6 FAULTS=0");
        }
        finally
        {
            session.UndoToMark(mark, null);
            session.DeleteUndoMark(mark, null);
        }
        if (part.Bodies.ToArray().Single().GetFaces().Length != faceCount)
            throw new Exception("Undo did not restore original topology.");
        report.WriteLine("UNDO=PASS FACES=" + faceCount);
        report.WriteLine("PASS");
        part.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null);
    }
}
