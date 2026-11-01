using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NXOpen;
using NXOpen.UF;

// Exercises the production Apply callback with real NX geometry, including
// partial success. It does not launch the interactive Block Styler event loop.
public static class NxFillHolesApplyRegression
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Type type;
    private static object context;
    private static TextWriter report;
    private static void Set(object instance, string name, object value)
    { type.GetField(name, Hidden).SetValue(instance, value); }
    private static object Call(object instance, string name, params object[] args)
    {
        try { return type.GetMethod(name, Hidden).Invoke(instance, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static object Create()
    {
        object instance = FormatterServices.GetUninitializedObject(type);
        Set(instance, "context", context);
        return instance;
    }
    private static HashSet<Tag> Tags(Part part)
    { return new HashSet<Tag>(part.Bodies.ToArray().SelectMany(b => b.GetFaces()).Select(f => f.Tag)); }
    private static double[][] EdgeSamples(UFSession uf, Tag tag)
    {
        IntPtr evaluator=IntPtr.Zero;
        try
        {
            uf.Eval.Initialize(tag,out evaluator);var limits=new double[2];uf.Eval.AskLimits(evaluator,limits);
            return Enumerable.Range(0,9).Select(i=>{var point=new double[3];uf.Eval.Evaluate(evaluator,0,limits[0]+(limits[1]-limits[0])*i/8.0,point,new double[3]);return point;}).ToArray();
        }
        finally{if(evaluator!=IntPtr.Zero)uf.Eval.Free(evaluator);}
    }
    private static object Prepare(Body body, Face[][] groups)
    {
        object instance = Create();
        Set(instance, "radiusTimer", new System.Windows.Forms.Timer());
        Set(instance, "previewTimer", new System.Windows.Forms.Timer());
        Set(instance, "bodies", new Dictionary<Tag, Body> { { body.Tag, body } });
        Set(instance, "groups", new List<Face[]>(groups));
        Set(instance, "retained", new HashSet<Tag>(groups.SelectMany(g => g).Select(f => f.Tag)));
        Set(instance, "radiusText", "4");
        Set(instance, "activeMaxRadius", 4.0);
        Set(instance, "previewValid", true);
        Set(instance, "ready", true);
        return instance;
    }
    private static void DisposeTimers(object instance)
    {
        ((IDisposable)type.GetField("radiusTimer", Hidden).GetValue(instance)).Dispose();
        ((IDisposable)type.GetField("previewTimer", Hidden).GetValue(instance)).Dispose();
    }
    public static void Main(string[] args)
    {
        using (var writer = new StreamWriter(args[2]))
        {
            writer.AutoFlush = true; report = writer;
            try { Run(args); }
            catch (Exception ex) { report.WriteLine("FATAL=" + ex); throw; }
        }
    }
    private static void Run(string[] args)
    {
        Session session = Session.GetSession();
        UFSession uf = UFSession.GetUFSession();
        PartLoadStatus status;
        Part part = session.Parts.OpenDisplay(Path.GetFullPath(args[0]), out status);
        status.Dispose();
        Assembly plugin = Assembly.LoadFrom(Path.GetFullPath(args[1]));
        type = plugin.GetType("NXRefine.UI.FillHolesDialog", true);
        context = plugin.GetType("NXRefine.Core.NxContext").GetProperty("Current").GetValue(null, null);
        Body body = part.Bodies.ToArray().Single(b => b.Tag.ToString() == args[3]);
        HashSet<Tag> original = Tags(part);
        var protectedFaces=new Dictionary<Tag,Tag[]>();
        var protectedEdges=new Dictionary<Tag,double[][]>();
        if(args.Length>9)
        {
            var ids=new HashSet<string>(File.ReadAllLines(args[9]).Where(line=>!string.IsNullOrWhiteSpace(line)&&!line.StartsWith("#")));
            Face[] protectedRegion=body.GetFaces().Where(face=>ids.Contains(face.Tag.ToString())).ToArray();
            if(protectedRegion.Length!=ids.Count)throw new Exception("Protected rib-end faces are missing from the fixture.");
            foreach(Face face in protectedRegion)
            {
                protectedFaces[face.Tag]=face.GetEdges().Select(edge=>edge.Tag).ToArray();
                foreach(Edge edge in face.GetEdges())protectedEdges[edge.Tag]=EdgeSamples(uf,edge.Tag);
            }
        }
        int originalBodies = part.Bodies.ToArray().Length;
        int originalSolids = part.Bodies.ToArray().Count(b => b.IsSolidBody);
        int originalFaults; int[] faults; Tag[] faultObjects;
        uf.Modl.AskBodyConsistency(body.Tag, out originalFaults, out faults, out faultObjects);
        report.WriteLine("SYSLOG=" + session.LogFile.FileName);
        Session.UndoMarkId outer = session.SetUndoMark(Session.MarkVisibility.Invisible, "Fill Holes Apply regression");
        try
        {
            Face[][] groups;
            bool cached = args.Length > 6 && File.Exists(args[6]);
            if (cached)
            {
                var byTag = body.GetFaces().ToDictionary(f => f.Tag.ToString());
                groups = File.ReadAllLines(args[6]).Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => line.Split(',').Select(id => byTag[id.Trim()]).ToArray()).ToArray();
            }
            else groups = (Face[][])Call(Create(), "FindGroups", body, 4.0);
            report.WriteLine("RECOGNIZED_GROUPS=" + groups.Length);
            if (groups.Length < 2) throw new Exception("Fixture must expose at least two separate healable hole groups.");
            bool allReviewed = args.Length > 5 && args[5] == "all-reviewed";
            bool stopAfterSuccess = args.Length > 8 && args[8] == "stop-after-success";
            int expectedStatus = args.Length > 4 ? int.Parse(args[4]) : 0;
            var borePoints = new List<double[]>();
            if (cached && allReviewed && args.Length > 7 && (int.Parse(args[7]) >= 0 || stopAfterSuccess))
            {
                foreach (Face[] group in groups)
                {
                    report.WriteLine("BORE_SAMPLE_GROUP=" + string.Join(",",group.Select(f=>f.Tag)));
                    double[] borePoint = null;
                    foreach (Face face in group.Where(f => f.SolidFaceType != Face.FaceType.Planar))
                    {
                        object instance = Create();
                        bool analytic = face.SolidFaceType == Face.FaceType.Cylindrical || face.SolidFaceType == Face.FaceType.Conical;
                        object[] values = analytic ? new object[] { face, body, null } : new object[] { face, body, 0.01, 4.0, null };
                        if (!(bool)Call(instance, analytic ? "TryGetInnerRevolvedFace" : "TryFitInnerRevolvedWall", values)) continue;
                        object seed = values[values.Length - 1];
                        double[] origin = (double[])seed.GetType().GetProperty("AxisPoint").GetValue(seed, null);
                        double[] axis = (double[])seed.GetType().GetProperty("Axis").GetValue(seed, null);
                        var uv = new double[4]; uf.Modl.AskFaceUvMinmax(face.Tag, uv);
                        var point = new double[3];
                        uf.Modl.AskFaceProps(face.Tag, new[] { (uv[0] + uv[1]) / 2, (uv[2] + uv[3]) / 2 }, point,
                            new double[3], new double[3], new double[3], new double[3], new double[3], new double[2]);
                        double along = Enumerable.Range(0, 3).Sum(i => (point[i] - origin[i]) * axis[i]) / axis.Sum(x => x * x);
                        var candidate = Enumerable.Range(0, 3).Select(i => origin[i] + along * axis[i]).ToArray();
                        int containment; uf.Modl.AskPointContainment(candidate, body.Tag, out containment);
                        report.WriteLine(" BORE_SAMPLE_FACE=" + face.Tag + " CONTAINMENT=" + containment + " POINT=" + string.Join(",",candidate));
                        if (containment == 2) { borePoint = candidate; break; }
                    }
                    if (borePoint == null)
                    foreach (Face floor in group.Where(f=>f.SolidFaceType==Face.FaceType.Planar))
                    {
                        object[] values={floor,0.01,new Dictionary<Tag,double[][]>(),null};
                        if(!(bool)Call(Create(),"TryGetCircularFloor",values))continue;
                        object seed=values[3];
                        double[] origin=(double[])seed.GetType().GetProperty("AxisPoint").GetValue(seed,null);
                        double[] axis=(double[])seed.GetType().GetProperty("Axis").GetValue(seed,null);
                        double radius=(double)seed.GetType().GetProperty("Radius").GetValue(seed,null);
                        foreach(double sign in new[]{1.0,-1.0})
                        {
                            var candidate=Enumerable.Range(0,3).Select(i=>origin[i]+sign*radius*0.25*axis[i]).ToArray();
                            int containment;uf.Modl.AskPointContainment(candidate,body.Tag,out containment);
                            report.WriteLine(" BORE_SAMPLE_FLOOR="+floor.Tag+" CONTAINMENT="+containment+" POINT="+string.Join(",",candidate));
                            if(containment==2){borePoint=candidate;break;}
                        }
                        if(borePoint!=null)break;
                    }
                    if (borePoint == null) throw new Exception("Could not establish an empty bore point before filling.");
                    borePoints.Add(borePoint);
                }
                report.WriteLine("EMPTY_BORE_POINTS=" + borePoints.Count);
            }
            Face[][] selected = allReviewed ? groups : new[] { groups[0], groups[0], groups[1] };
            object dialog = Prepare(body, selected);
            try
            {
                bool stopSent=false;
                Exception stopFailure=null;
                var finished=new System.Threading.ManualResetEvent(false);
                System.Threading.Thread stopper=null;
                if(stopAfterSuccess)
                {
                    stopper=new System.Threading.Thread(delegate() {
                        try
                        {
                            while(!finished.WaitOne(5))
                            {
                                object scan=type.GetField("activeDeleteScan",Hidden).GetValue(dialog);
                                int count=(int)type.GetField("deleteSucceeded",Hidden).GetValue(dialog);
                                if(scan==null || count==0)continue;
                                // Identical managed request used by the Stop button;
                                // this worker never calls NX APIs.
                                scan.GetType().GetMethod("RequestStop",Hidden).Invoke(scan,null);
                                stopSent=true;return;
                            }
                        }
                        catch(Exception ex){stopFailure=ex;}
                    });
                    stopper.IsBackground=true;stopper.Start();
                }
                var applyClock=System.Diagnostics.Stopwatch.StartNew();
                int result;
                try { result = (int)Call(dialog, "Apply"); }
                finally { finished.Set();if(stopper!=null)stopper.Join(5000);finished.Dispose(); }
                report.WriteLine("APPLY_MS="+applyClock.ElapsedMilliseconds);
                if(stopFailure!=null)throw new Exception("Stop worker failed.",stopFailure);
                HashSet<Tag> live = Tags(part);
                // Count distinct reviewed groups; the repeated first group in
                // the small fixture intentionally fails after its initial heal.
                int removedGroups = groups.Count(g => g.All(f => !live.Contains(f.Tag)));
                int remainingGroups = groups.Count(g => g.Any(f => live.Contains(f.Tag)));
                report.WriteLine("PARTIAL_APPLY STATUS=" + result + " REMOVED_GROUPS=" + removedGroups +
                    " REMAINING_GROUPS=" + remainingGroups + " REMOVED_FACES=" + original.Count(t => !live.Contains(t)));
                if (result != expectedStatus) throw new Exception("Partial Apply returned the wrong callback status.");
                if(stopAfterSuccess)
                {
                    if(!stopSent || removedGroups==0 || remainingGroups==0)throw new Exception("Stop did not preserve a partial completed result.");
                    var pending=(List<Face[]>)type.GetField("groups",Hidden).GetValue(dialog);
                    if(pending.Count!=remainingGroups)throw new Exception("Stop did not retain the remaining reviewed groups for continuation.");
                    report.WriteLine("STOP_AFTER_SUCCESS=PASS COMPLETED="+removedGroups+" UNPROCESSED="+remainingGroups);
                    report.WriteLine("STOP_PENDING_GROUPS_RETAINED=True");
                }
                if (!allReviewed && removedGroups != 2) throw new Exception("The failed group undid a previously successful group.");
                if (allReviewed && cached && removedGroups == 0 && !(args.Length>7 && int.Parse(args[7])==0))
                    throw new Exception("No cached reviewed hole group was healed.");
                if (allReviewed && args.Length > 7 && int.Parse(args[7]) >= 0 && removedGroups != int.Parse(args[7]))
                    throw new Exception("The reviewed target coverage did not match the expected healed group count.");
                int filledSamples=0;
                for(int i=0;i<borePoints.Count;i++)
                {
                    int containment; uf.Modl.AskPointContainment(borePoints[i], body.Tag, out containment);
                    bool removed=groups[i].All(face=>!live.Contains(face.Tag));
                    if (removed && containment != 1) throw new Exception("A removed hole group left its sampled bore empty.");
                    if (!removed && containment != 2) throw new Exception("A skipped hole's empty bore point changed.");
                    if(removed)filledSamples++;
                }
                if (borePoints.Count > 0) report.WriteLine("FILLED_BORE_POINTS=" + filledSamples+" SKIPPED_BORE_POINTS_PRESERVED="+(borePoints.Count-filledSamples));
                if(protectedFaces.Count>0)
                {
                    var current=body.GetFaces().ToDictionary(face=>face.Tag);
                    foreach(var pair in protectedFaces)
                    {
                        Face face;
                        if(!current.TryGetValue(pair.Key,out face)||!new HashSet<Tag>(pair.Value).SetEquals(face.GetEdges().Select(edge=>edge.Tag)))
                            throw new Exception("A protected rib end was deleted or its boundary changed.");
                    }
                    foreach(var pair in protectedEdges)
                    {
                        double[][] points=EdgeSamples(uf,pair.Key);
                        if(Enumerable.Range(0,points.Length).Any(i=>Enumerable.Range(0,3).Sum(j=>Math.Pow(points[i][j]-pair.Value[i][j],2))>1e-12))
                            throw new Exception("A protected rib-end curve moved.");
                    }
                    report.WriteLine("PROTECTED_RIB_ENDS_UNCHANGED="+protectedFaces.Count);
                }
                if (allReviewed && !cached && (removedGroups != 18 || remainingGroups != 3))
                    throw new Exception("Expected the user's rearLamp housing result: 18 healed, 3 skipped.");
                if (result == 0)
                {
                    if(stopAfterSuccess)
                    {
                        if((int)Call(dialog,"Apply")!=0)throw new Exception("The retained holes could not be resumed.");
                        live=Tags(part);
                        if(groups.Any(group=>group.Any(face=>live.Contains(face.Tag))))throw new Exception("Resume left a reviewed target hole unfilled.");
                        foreach(double[] point in borePoints)
                        {
                            int containment;uf.Modl.AskPointContainment(point,body.Tag,out containment);
                            if(containment!=1)throw new Exception("Resume did not fill every original bore sample.");
                        }
                        report.WriteLine("RESUME_WITHOUT_RESCAN=PASS");
                    }
                    Call(dialog, "Cancel");
                    if (!live.SetEquals(Tags(part))) throw new Exception("Cancel changed the accepted Apply result.");
                    report.WriteLine("CANCEL_AFTER_ACCEPTED_APPLY_PRESERVED=True");
                }
                else
                {
                    // Reproduce the caller's rejected-action rollback. This
                    // explicitly models the return-code contract, not UI clicks.
                    session.UndoToMark(outer, null);
                    if (!original.SetEquals(Tags(part))) throw new Exception("Baseline rollback did not restore the holes.");
                    report.WriteLine("REJECTED_CALLBACK_ROLLBACK_RESTORED_HOLES=True");
                }
            }
            finally { DisposeTimers(dialog); }

            if (!allReviewed)
            {
                session.UndoToMark(outer, null);
                dialog = Prepare(body, new[] { groups[0], groups[1] });
                try
                {
                    if ((int)Call(dialog, "Apply") != 0) throw new Exception("All-success Apply was rejected.");
                    report.WriteLine("ALL_SUCCESS_STATUS=0");
                }
                finally { DisposeTimers(dialog); }
                // Both groups are now stale. An all-failed Apply must return
                // failure without reverting the already accepted operation.
                HashSet<Tag> accepted = Tags(part);
                dialog = Prepare(body, new[] { groups[0], groups[1] });
                try
                {
                    if ((int)Call(dialog, "Apply") == 0) throw new Exception("All-failed Apply was accepted.");
                    if (!accepted.SetEquals(Tags(part))) throw new Exception("All-failed Apply undid earlier accepted holes.");
                    report.WriteLine("ALL_FAILED_STATUS=1 PRIOR_SUCCESS_PRESERVED=True");
                }
                finally { DisposeTimers(dialog); }
            }
            int currentFaults; uf.Modl.AskBodyConsistency(body.Tag, out currentFaults, out faults, out faultObjects);
            if (part.Bodies.ToArray().Length != originalBodies || part.Bodies.ToArray().Count(b => b.IsSolidBody) != originalSolids ||
                currentFaults > originalFaults) throw new Exception("Apply changed solid counts or introduced body consistency faults.");
            report.WriteLine("BODY_CONSISTENCY=PASS");
        }
        finally
        {
            session.UndoToMark(outer, null); session.DeleteUndoMark(outer, null);
            bool restored = original.SetEquals(Tags(part));
            report.WriteLine("UNDO_RESTORED=" + restored);
            part.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null);
            report.WriteLine("INPUT_NOT_SAVED=True");
            File.Copy(session.LogFile.FileName, Path.ChangeExtension(args[2], ".syslog"), true);
            if (!restored) throw new Exception("Undo failed to restore the input topology.");
        }
    }
}
