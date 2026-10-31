using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NXOpen;
using NXOpen.UF;

// Isolated, unsaved diagnosis of the production recognition/deletion methods.
public static class NxCleanupDiagnosis
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static TextWriter report;
    private static Assembly plugin;
    private static object context;
    private static object Create(string name)
    {
        Type type = plugin.GetType("NXRefine.UI." + name, true);
        object instance = FormatterServices.GetUninitializedObject(type);
        type.GetField("context", Hidden).SetValue(instance, context);
        return instance;
    }
    private static object Call(object instance, string name, params object[] args)
    {
        try { return instance.GetType().GetMethod(name, Hidden).Invoke(instance, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Set(object instance, string name, object value)
    { instance.GetType().GetField(name, Hidden).SetValue(instance, value); }
    private static Face[] Faces(Part part)
    { return part.Bodies.ToArray().Where(b => b.IsSolidBody).SelectMany(b => b.GetFaces()).ToArray(); }
    private static bool Include(Body body, string[] args)
    { return args.Length < 5 || args[4].Length == 0 || args[4] == "all" || body.Tag.ToString() == args[4]; }
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
        if (args.Length < 4) throw new ArgumentException("Expected part, plugin, report, mode, optional body tag and maximum groups.");
        Session session = Session.GetSession();
        UFSession uf = UFSession.GetUFSession();
        Stopwatch clock = Stopwatch.StartNew();
        PartLoadStatus status;
        Part part = session.Parts.OpenDisplay(Path.GetFullPath(args[0]), out status);
        for (int i = 0; i < status.NumberUnloadedParts; i++)
            report.WriteLine("LOAD_WARNING=" + status.GetPartName(i) + ":" + status.GetStatusDescription(i));
        status.Dispose();
        report.WriteLine("OPEN_MS=" + clock.ElapsedMilliseconds + " UNITS=" + part.PartUnits);
        report.WriteLine("SYSLOG=" + session.LogFile.FileName);
        report.WriteLine("MODE=" + args[3] + " BODY_FILTER=" + (args.Length > 4 ? args[4] : "all"));
        plugin = Assembly.LoadFrom(Path.GetFullPath(args[1]));
        context = plugin.GetType("NXRefine.Core.NxContext").GetProperty("Current").GetValue(null, null);
        var original = new HashSet<Tag>(Faces(part).Select(f => f.Tag));
        int bodyCount = part.Bodies.ToArray().Length;
        Session.UndoMarkId outer = session.SetUndoMark(Session.MarkVisibility.Invisible, "Cleanup diagnosis");
        try
        {
            if (args.Length > 4 && args[4].Length > 0 && args[4] != "all" && !part.Bodies.ToArray().Any(b => b.Tag.ToString() == args[4]))
                throw new ArgumentException("The body tag is not present in this load; inspect the audit report for current tags.");
            report.WriteLine("BODIES=" + bodyCount + " SOLIDS=" + part.Bodies.ToArray().Count(b => b.IsSolidBody));
            foreach (Body body in part.Bodies.ToArray())
            {
                Face[] faces = body.GetFaces();
                report.WriteLine("BODY=" + body.Tag + " NAME=" + body.Name + " SOLID=" + body.IsSolidBody +
                    " OCCURRENCE=" + body.IsOccurrence + " FACES=" + faces.Length + " TYPES=" +
                    string.Join(",", faces.GroupBy(f => f.SolidFaceType).Select(g => g.Key + ":" + g.Count())));
            }
            if(args[3]=="hole-target-recovery")
            {
                object holes=Create("FillHolesDialog");var ids=new HashSet<string>(File.ReadAllLines(args[6]));
                foreach(Body body in part.Bodies.ToArray().Where(b=>b.IsSolidBody))
                {
                    var selected=body.GetFaces().Where(f=>ids.Contains(f.Tag.ToString()))
                        .SelectMany(f=>new[]{f}.Concat(f.GetEdges().SelectMany(e=>e.GetFaces())))
                        .GroupBy(f=>f.Tag).Select(g=>g.First()).ToArray();
                    var groups=(Face[][])Call(holes,"FindCompositeHoleGroups",selected,body,4.0);
                    report.WriteLine("TARGET_RECOVERED="+groups.Length);
                    foreach(Face[] group in groups) report.WriteLine("RECOVERED_GROUP="+string.Join(",",group.Select(f=>f.Tag)));
                }
            }
            else if(args[3]=="hole-targets")
            {
                object holes=Create("FillHolesDialog");var faces=Faces(part).ToDictionary(f=>f.Tag.ToString());
                double tolerance;uf.Modl.AskDistanceToleranceOfPart(part.Tag,out tolerance);report.WriteLine("PART_TOL="+tolerance);
                foreach(string id in File.ReadAllLines(args[6]))
                {
                    Face f;if(!faces.TryGetValue(id,out f))continue;
                    object[] values=f.SolidFaceType==Face.FaceType.Planar?new object[]{f,0.01,new Dictionary<Tag,double[][]>(),null}:new object[]{f,f.GetBody(),0.01,4.0,null};
                    bool fit=(bool)Call(holes,f.SolidFaceType==Face.FaceType.Planar?"TryGetCircularFloor":"TryFitInnerRevolvedWall",values);
                    report.WriteLine("TARGET="+id+" TYPE="+f.SolidFaceType+" FIT="+fit);
                    if(fit)
                    {
                        object seed=values[values.Length-1];
                        foreach(var field in seed.GetType().GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))
                            if(field.FieldType==typeof(double[]))report.WriteLine(" SEED_"+field.Name+"="+string.Join(",",(double[])field.GetValue(seed)));
                    }
                    foreach(Edge edge in f.GetEdges())
                    {
                        double tol;uf.Modl.AskEdgeTolerance(edge.Tag,out tol);report.WriteLine(" EDGE="+edge.Tag+" TOL="+tol+" FACES="+string.Join(",",edge.GetFaces().Select(x=>x.Tag)));
                        IntPtr eval;uf.Eval.Initialize(edge.Tag,out eval);
                        try{var limits=new double[2];uf.Eval.AskLimits(eval,limits);foreach(int k in new[]{0,4,8,12,16}){var p=new double[3];uf.Eval.Evaluate(eval,0,limits[0]+(limits[1]-limits[0])*k/16.0,p,new double[3]);report.WriteLine("  P="+string.Join(",",p));}}
                        finally{uf.Eval.Free(eval);}
                    }
                }
            }
            else if(args[3]=="hole-picks")
            {
                object holes=Create("FillHolesDialog");
                var boxes=Faces(part).Select(face=>{var box=new double[6];uf.Modl.AskBoundingBox(face.Tag,box);return new{face,box};}).ToArray();
                var seen=new HashSet<Tag>();
                foreach(string line in File.ReadAllLines(args[6]))
                {
                    var fields=line.Split(',');var point=fields.Skip(1).Select(x=>double.Parse(x,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    var closest=boxes.OrderBy(item=>Enumerable.Range(0,3).Sum(i=>Math.Pow(Math.Max(0,Math.Max(item.box[i]-point[i],point[i]-item.box[i+3])),2))).Take(24)
                        .Select(item=>{double distance,accuracy;uf.Modl.AskMinimumDist3(2,Tag.Null,item.face.Tag,1,point,0,new double[3],out distance,new double[3],new double[3],out accuracy);return new{item.face,distance};}).OrderBy(item=>item.distance).First();
                    report.WriteLine("PICK="+fields[0]+" FACE="+closest.face.Tag+" TYPE="+closest.face.SolidFaceType+" DIST="+closest.distance);
                    if(!seen.Add(closest.face.Tag))continue;
                    foreach(Face f in new[]{closest.face}.Concat(closest.face.GetEdges().SelectMany(e=>e.GetFaces())).GroupBy(f=>f.Tag).Select(g=>g.First()))
                    {
                        object[] inner={f,f.GetBody(),null};var uv=new double[4];uf.Modl.AskFaceUvMinmax(f.Tag,uv);
                        report.WriteLine(" DETAIL="+f.Tag+" TYPE="+f.SolidFaceType+" INNER="+Call(holes,"TryGetInnerRevolvedFace",inner)+" MAX="+Call(holes,"GetRevolvedMaximumRadius",f)+" SPAN="+(uv[1]-uv[0]));
                    }
                    var native=(Face[])Call(holes,"FindNativeHoleFaces",closest.face);
                    report.WriteLine(" PICK_NATIVE="+string.Join(",",native.Select(f=>f.Tag)));
                }
            }
            else if(args[3]=="hole-recovery" || args[3]=="hole-circles")
            {
                object holes=Create("FillHolesDialog");
                foreach(Body body in part.Bodies.ToArray().Where(b=>b.IsSolidBody))
                foreach(string method in new[]{"FindCompositeHoleGroups"})
                {
                    Face[][] recovered=(Face[][])Call(holes,method,body.GetFaces(),body,4.0);
                    report.WriteLine("RECOVERY="+method+" GROUPS="+recovered.Length);
                    foreach(Face[] group in recovered) report.WriteLine("RECOVERED_GROUP="+string.Join(",",group.Select(f=>f.Tag)));
                }
            }
            else if(args[3]=="mesh")
            {
                var exporter=session.DexManager.CreateStlCreator();
                try {exporter.OutputType=STLCreator.OutputTypeEnum.Binary; exporter.ChordalTol=0.15; exporter.AngularTol=15;
                    exporter.OutputFile=Path.ChangeExtension(args[2],".stl");
                    foreach(Body body in part.Bodies.ToArray().Where(b=>b.IsSolidBody)) exporter.ExportSelectionBlock.Add(body);
                    exporter.Commit(); report.WriteLine("MESH_EXPORTED=True");}
                finally {exporter.Destroy();}
            }
            else if (args[3] == "hole-walls" || args[3] == "hole-neighbors")
            {
                object holes = Create("FillHolesDialog");
                foreach (Body body in part.Bodies.ToArray().Where(b => b.IsSolidBody && Include(b, args)))
                foreach (Face face in body.GetFaces())
                {
                    if (face.SolidFaceType != Face.FaceType.Cylindrical && face.SolidFaceType != Face.FaceType.Conical) continue;
                    double[] uv = new double[4]; uf.Modl.AskFaceUvMinmax(face.Tag, uv);
                    object[] inner = { face, body, null };
                    if (!(bool)Call(holes, "TryGetInnerRevolvedFace", inner)) continue;
                    double maximum = (double)Call(holes, "GetRevolvedMaximumRadius", face);
                    if (args[3] == "hole-neighbors")
                    {
                        if (face.SolidFaceType!=Face.FaceType.Cylindrical || maximum<1.5 || maximum>5 || (bool)Call(holes,"HasFullAngularSpan",face)) continue;
                    }
                    else if (uv[1]-uv[0] < 3 || maximum > 15) continue;
                    double[] point=new double[3], axis=new double[3], box=new double[6]; int ft, nd; double r, rd;
                    uf.Modl.AskFaceData(face.Tag,out ft,point,axis,box,out r,out rd,out nd);
                    int up,vp; double uPeriod,vPeriod; uf.Modl.AskFacePeriodicity(face.Tag,out up,out uPeriod,out vp,out vPeriod);
                    report.WriteLine("WALL="+face.Tag+" TYPE="+face.SolidFaceType+" R="+r+" MAX="+maximum+" SPAN="+(uv[1]-uv[0])+" UPERIOD="+uPeriod+" AXIS="+string.Join(",",axis)+" BOX="+string.Join(",",box));
                    if(args[3]=="hole-neighbors")
                    {
                        foreach(Face neighbor in face.GetEdges().SelectMany(e=>e.GetFaces()).Where(f=>f.Tag!=face.Tag).GroupBy(f=>f.Tag).Select(g=>g.First()))
                            report.WriteLine(" NEIGHBOR="+neighbor.Tag+" TYPE="+neighbor.SolidFaceType+" R="+Call(holes,"GetRevolvedMaximumRadius",neighbor)+" ADJ="+string.Join(",",neighbor.GetEdges().SelectMany(e=>e.GetFaces()).Select(f=>f.Tag).Distinct()));
                        continue;
                    }
                    Face[] native=(Face[])Call(holes,"FindNativeHoleFaces",face);
                    foreach(Face member in native)
                    {
                        bool analytic=member.SolidFaceType==Face.FaceType.Cylindrical||member.SolidFaceType==Face.FaceType.Conical;
                        object[] mi={member,body,null};
                        report.WriteLine(" MEMBER="+member.Tag+" TYPE="+member.SolidFaceType+" FULL="+(analytic?(bool)Call(holes,"HasFullAngularSpan",member):false)+" INNER="+(bool)Call(holes,"TryGetInnerRevolvedFace",mi)+" MAX="+Call(holes,"GetRevolvedMaximumRadius",member));
                    }
                }
            }
            else if (args[3] == "probes" || args[3] == "split-probes")
            {
                object holes = Create("FillHolesDialog");
                foreach (Body body in part.Bodies.ToArray().Where(b => b.IsSolidBody && Include(b, args)))
                {
                    int tested = 0;
                    var seen = new HashSet<Tag>();
                    foreach (Face face in body.GetFaces())
                    {
                        if (seen.Contains(face.Tag)) continue;
                        if (face.SolidFaceType != Face.FaceType.Cylindrical && face.SolidFaceType != Face.FaceType.Conical) continue;
                        object[] innerArgs = { face, body, null };
                        if (!(bool)Call(holes, "TryGetInnerRevolvedFace", innerArgs)) continue;
                        if (args[3] == "split-probes")
                        {
                            double[] seedUv = new double[4]; uf.Modl.AskFaceUvMinmax(face.Tag, seedUv);
                            double seedRadius = (double)Call(holes, "GetRevolvedMaximumRadius", face);
                            if (Math.Abs(seedUv[1] - seedUv[0] - Math.PI) > 1e-5 || seedRadius < 1 || seedRadius > 4) continue;
                        }
                        clock.Restart();
                        report.WriteLine("NATIVE_HOLE_START BODY=" + body.Tag + " SEED=" + face.Tag);
                        Face[] group = (Face[])Call(holes, "FindNativeHoleFaces", face);
                        foreach (Face item in group) seen.Add(item.Tag);
                        var wallInfo = new List<string>();
                        foreach (Face item in group.Where(f => f.SolidFaceType == Face.FaceType.Cylindrical || f.SolidFaceType == Face.FaceType.Conical))
                        {
                            double[] uv = new double[4]; uf.Modl.AskFaceUvMinmax(item.Tag, uv);
                            object[] memberInner = { item, body, null };
                            bool isInner = (bool)Call(holes, "TryGetInnerRevolvedFace", memberInner);
                            wallInfo.Add(item.Tag + ":" + item.SolidFaceType + ":span=" + (uv[1] - uv[0]).ToString("R") +
                                ":radius=" + Call(holes, "GetRevolvedMaximumRadius", item) + ":inner=" + isInner);
                        }
                        report.WriteLine("NATIVE_HOLE BODY=" + body.Tag + " SEED=" + face.Tag + " COUNT=" + group.Length +
                            " MS=" + clock.ElapsedMilliseconds + " WALLS=" + string.Join(";", wallInfo));
                        if (group.Length > 0 && group.Length < body.GetFaces().Length)
                        {
                            Tag[] tags = group.Select(f => f.Tag).ToArray();
                            Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "Native hole probe");
                            try
                            {
                                int beforeFaults; int[] faults; Tag[] faultObjects;
                                uf.Modl.AskBodyConsistency(body.Tag, out beforeFaults, out faults, out faultObjects);
                                Call(holes, "DeleteReviewedHoleFaces", new object[] { group });
                                var live = new HashSet<Tag>(Faces(part).Select(f => f.Tag));
                                int afterFaults; uf.Modl.AskBodyConsistency(body.Tag, out afterFaults, out faults, out faultObjects);
                                report.WriteLine("NATIVE_HOLE_DELETE=COMMITTED REMOVED=" + tags.Count(t => !live.Contains(t)) +
                                    " SELECTED=" + tags.Length + " BODY_SOLID=" + body.IsSolidBody + " FAULTS=" + beforeFaults + "->" + afterFaults);
                            }
                            catch (Exception ex) { report.WriteLine("NATIVE_HOLE_DELETE=FAILED " + ex.Message); }
                            finally { session.UndoToMark(mark, null); session.DeleteUndoMark(mark, null); }
                        }
                        if (++tested == (args.Length > 5 ? int.Parse(args[5]) : 8)) break;
                    }
                    report.WriteLine("PROBES_TESTED BODY=" + body.Tag + " COUNT=" + tested);
                }
            }
            else if (args[3] == "audit")
            {
                object holes = Create("FillHolesDialog");
                object blends = Create("RemoveBlendsDialog");
                foreach (Body body in part.Bodies.ToArray().Where(b => b.IsSolidBody && Include(b, args)))
                {
                    var counts = new Dictionary<string, int>();
                    var radii = new List<double>();
                    clock.Restart();
                    foreach (Face face in body.GetFaces())
                    {
                        int ft, nd; double radius, rd;
                        uf.Modl.AskFaceData(face.Tag, out ft, new double[3], new double[3], new double[6], out radius, out rd, out nd);
                        Count(counts, "UF" + ft);
                        object[] blendArgs = { face, 0.0, false };
                        if ((bool)Call(blends, "TryGetBlend", blendArgs))
                        {
                            radii.Add((double)blendArgs[1]);
                            Count(counts, (double)blendArgs[1] <= 3.0 ? "BlendWithin3" : "BlendAbove3");
                        }
                        if (face.SolidFaceType != Face.FaceType.Cylindrical && face.SolidFaceType != Face.FaceType.Conical) continue;
                        Count(counts, "AnalyticWall");
                        bool full = (bool)Call(holes, "HasFullAngularSpan", face);
                        object[] innerArgs = { face, body, null };
                        bool inner = (bool)Call(holes, "TryGetInnerRevolvedFace", innerArgs);
                        double maximum = (double)Call(holes, "GetRevolvedMaximumRadius", face);
                        if (full) Count(counts, "FullAngular");
                        if (inner) Count(counts, "Inner");
                        if (inner && full) Count(counts, "FullInner");
                        if (inner && !full) Count(counts, "SplitOrPartialInner");
                        if (inner && full && maximum <= 4.0) Count(counts, "FullInnerWithin4");
                    }
                    report.WriteLine("AUDIT BODY=" + body.Tag + " COUNTS=" + string.Join(",", counts.Select(kv => kv.Key + ":" + kv.Value)) +
                        " BLEND_RADII=" + string.Join(",", radii.GroupBy(r => Math.Round(r, 4)).Select(g => g.Key + ":" + g.Count())) + " MS=" + clock.ElapsedMilliseconds);
                }
            }
            else if (args[3] == "holes" || args[3] == "holes-scan")
            {
                object holes = Create("FillHolesDialog");
                foreach (Body body in part.Bodies.ToArray().Where(b => b.IsSolidBody && Include(b, args)))
                {
                    clock.Restart();
                    report.WriteLine("HOLES_START BODY=" + body.Tag);
                    Face[][] groups = (Face[][])Call(holes, "FindGroups", body, 4.0);
                    report.WriteLine("HOLES BODY=" + body.Tag + " GROUPS=" + groups.Length + " MS=" + clock.ElapsedMilliseconds);
                    foreach (Face[] group in groups)
                        report.WriteLine("HOLE_GROUP=" + string.Join(",", group.Select(f => f.Tag).OrderBy(t => t)));
                    if (args[3] == "holes-scan")
                    {
                        if (args.Length > 6)
                        {
                            foreach (string raw in File.ReadAllLines(args[6]).Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#")))
                            {
                                bool excluded = raw.StartsWith("!");
                                var ids = new HashSet<string>((excluded ? raw.Substring(1) : raw).Split(',').Select(id => id.Trim()));
                                bool matched = excluded ? groups.Any(group => group.Any(face => ids.Contains(face.Tag.ToString()))) :
                                    groups.Any(group => ids.SetEquals(group.Select(face => face.Tag.ToString())));
                                if (excluded ? matched : !matched) throw new Exception("Hole coverage expectation failed: " + raw);
                            }
                            report.WriteLine("COVERAGE_EXPECTATIONS=PASS");
                        }
                        continue;
                    }
                    int maximumGroups = args.Length > 5 ? int.Parse(args[5]) : 8;
                    foreach (Face[] group in groups.Take(maximumGroups))
                    {
                        Tag[] tags = group.Select(f => f.Tag).ToArray();
                        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "Hole probe");
                        try
                        {
                            int beforeFaults; int[] faults; Tag[] faultObjects;
                            uf.Modl.AskBodyConsistency(body.Tag, out beforeFaults, out faults, out faultObjects);
                            Call(holes, "DeleteReviewedHoleFaces", new object[] { group });
                            var live = new HashSet<Tag>(Faces(part).Select(f => f.Tag));
                            int removed = tags.Count(t => !live.Contains(t));
                            int afterFaults; uf.Modl.AskBodyConsistency(body.Tag, out afterFaults, out faults, out faultObjects);
                            report.WriteLine("HOLE_DELETE=COMMITTED SELECTED=" + group.Length + " REMOVED=" + removed +
                                " SOLIDS=" + part.Bodies.ToArray().Count(b => b.IsSolidBody) + " FAULTS=" + beforeFaults + "->" + afterFaults);
                        }
                        catch (Exception ex) { report.WriteLine("HOLE_DELETE=FAILED " + ex.Message); }
                        finally { session.UndoToMark(mark, null); session.DeleteUndoMark(mark, null); }
                    }
                }
            }
            else if (args[3] == "blends" || args[3] == "blends-direct")
            {
                object blends = Create("RemoveBlendsDialog");
                for (int round = 1; round <= 2; round++)
                {
                    clock.Restart();
                    Face[] seeds = Faces(part).Where(face => Include(face.GetBody(), args)).Where(face =>
                    {
                        object[] values = { face, 0.0, false };
                        return (bool)Call(blends, "TryGetBlend", values) && (bool)values[2] &&
                            (double)values[1] >= 0 && (double)values[1] <= 3.0;
                    }).ToArray();
                    Set(blends, "recognitionSeeds", seeds.Select(f => f.Tag).ToArray());
                    report.WriteLine("BLEND_RECOGNITION_START ROUND=" + round + " SEEDS=" + seeds.Length);
                    // Direct mode isolates deletion scheduling from the very
                    // expensive complete preview. DeleteBestEffort still builds
                    // and commits complete live native rules from these seeds.
                    Face[][] groups = args[3] == "blends-direct" ? seeds.Select(f => new[] { f }).ToArray() :
                        (Face[][])Call(blends, "BuildConnectedGroups", new object[] { seeds });
                    Tag[] selected = groups.SelectMany(g => g).Select(f => f.Tag).Distinct().ToArray();
                    report.WriteLine("BLENDS ROUND=" + round + " SEEDS=" + seeds.Length +
                        (args[3] == "blends-direct" ? " SEED_INPUT=" : " PREVIEW=") + selected.Length +
                        (args[3] == "blends-direct" ? " INPUT_BATCHES=" : " GROUPS=") + groups.Length + " SCAN_MS=" + clock.ElapsedMilliseconds +
                        " LARGEST=" + string.Join(",", groups.Select(g => g.Length).OrderByDescending(n => n).Take(10)));
                    clock.Restart();
                    int removed = (int)Call(blends, "DeleteBestEffort", new object[] { selected });
                    var live = new HashSet<Tag>(Faces(part).Select(f => f.Tag));
                    int actual = selected.Count(t => !live.Contains(t));
                    report.WriteLine("BLEND_DELETE ROUND=" + round + " REMOVED=" + removed + " MS=" + clock.ElapsedMilliseconds +
                        " ACTUAL=" + actual +
                        " ATTEMPTS=" + blends.GetType().GetField("deleteAttempts", Hidden).GetValue(blends) +
                        " BUDGET=" + blends.GetType().GetField("deleteBudgetReported", Hidden).GetValue(blends));
                    if (removed != actual) throw new InvalidOperationException("Reported removals differ from current topology.");
                }
            }
            else throw new ArgumentException("Unknown diagnosis mode: " + args[3]);
        }
        finally
        {
            session.UndoToMark(outer, null); session.DeleteUndoMark(outer, null);
            bool restored = original.SetEquals(Faces(part).Select(f => f.Tag)) && part.Bodies.ToArray().Length == bodyCount;
            report.WriteLine("UNDO_RESTORED=" + restored);
            part.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null);
            report.WriteLine("INPUT_NOT_SAVED=True");
            // NX can remove a batch session's temporary log on exit. Preserve
            // the native failure/timing evidence while this session is alive.
            try
            {
                string logCopy = Path.ChangeExtension(args[2], ".syslog");
                File.Copy(session.LogFile.FileName, logCopy, true);
                report.WriteLine("SYSLOG_COPY=" + logCopy);
            }
            catch (IOException ex) { report.WriteLine("SYSLOG_COPY_FAILED=" + ex.Message); }
            if (!restored) throw new InvalidOperationException("Undo did not restore original bodies and solid-face tags.");
        }
    }
    private static void Count(Dictionary<string, int> counts, string name)
    { int value; counts.TryGetValue(name, out value); counts[name] = value + 1; }
}
