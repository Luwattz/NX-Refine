using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using NXOpen;
using NXOpen.UF;
using NXRefine.Analysis;

// Optional NX kernel integration test. Run in a separate NXOpen process; only
// creates unsaved synthetic parts, never opens or modifies user part files.
internal static class NxGapProjectionTests
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }

    public static int Main(string[] args)
    {
        try
        {
            Session session = Session.GetSession();
            UFSession uf = UFSession.GetUFSession();
            foreach (bool inches in new[] { false, true })
            {
                double scale = inches ? 1.0 / 25.4 : 1.0;
                session.Parts.NewDisplay(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    "gap_projection_" + Guid.NewGuid().ToString("N") + ".prt"), inches ? Part.Units.Inches : Part.Units.Millimeters);
                Tag outer = Block(uf, new double[3], new[] { 10 * scale, 10 * scale, scale });
                Tag hole = Block(uf, new[] { 4 * scale, 4 * scale, -scale }, new[] { 2 * scale, 2 * scale, 3 * scale });
                int resultCount;
                Tag[] result;
                uf.Modl.SubtractBodies(outer, hole, out resultCount, out result);
                Check(resultCount == 1, "Synthetic frame is one solid");
                Tag[] faceTags;
                uf.Modl.AskBodyFaces(result[0], out faceTags);
                Tag top = Tag.Null;
                foreach (Tag face in faceTags)
                {
                    Tag[] adjacent, edges;
                    uf.Modl.AskAdjacFaces(face, out adjacent);
                    uf.Modl.AskFaceEdges(face, out edges);
                    var expectedNeighbors = new HashSet<Tag>();
                    foreach (Tag edge in edges)
                    {
                        Tag[] touching;
                        uf.Modl.AskEdgeFaces(edge, out touching);
                        foreach (Tag neighbor in touching) if (neighbor != face) expectedNeighbors.Add(neighbor);
                    }
                    var actualNeighbors = new HashSet<Tag>(adjacent ?? new Tag[0]);
                    actualNeighbors.Remove(face);
                    Check(actualNeighbors.SetEquals(expectedNeighbors), "Native adjacency matches edge traversal");
                    var box = new double[6];
                    uf.Modl.AskBoundingBox(face, box);
                    if (Math.Abs(box[2] - scale) < 1e-8 * scale && Math.Abs(box[5] - scale) < 1e-8 * scale) top = face;
                }
                Check(top != Tag.Null, "Found planar face with through hole");
                double[][] xy = { new[] { 2.0, 2.0 }, new[] { 5.0, 5.0 }, new[] { 4.0, 5.0 },
                    new[] { 0.0, 5.0 }, new[] { -1.0, 5.0 } };
                int[] expected = { 1, 2, 3, 3, 2 };
                double[] origin = { 0, 0, scale }, normal = { 0, 0, 1 };
                for (int i = 0; i < xy.Length; i++)
                {
                    double[] reference = { xy[i][0] * scale, xy[i][1] * scale, 1.005 * scale };
                    double[] projected;
                    double distance;
                    Check(GapCriteria.TryProjectToPlane(reference, origin, normal, 1e-6 * scale, out projected, out distance), "Plane projection succeeds");
                    int status;
                    uf.Modl.AskPointContainment(projected, top, out status);
                    Check(status == expected[i], "Trim classification " + i + " expected " + expected[i] + " got " + status);
                    double[] kernelPoint;
                    double kernelDistance = KernelDistance(uf, top, reference, out kernelPoint);
                    if (status == 1)
                    {
                        Check(Math.Abs(kernelDistance - distance) < 1e-7 * scale, "Interior projection matches NX minimum");
                        for (int k = 0; k < 3; k++) Check(Math.Abs(projected[k] - kernelPoint[k]) < 1e-7 * scale, "Interior projected point matches NX");
                    }
                    if (i == 1 || i == 4) Check(kernelDistance > distance + .1 * scale, "Hole/exterior requires kernel fallback");
                }
                const int repetitions = 500;
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < repetitions; i++)
                {
                    double[] unused;
                    KernelDistance(uf, top, new[] { (1.0 + i % 20 * .1) * scale, 2 * scale, 1.005 * scale }, out unused);
                }
                long kernelMs = timer.ElapsedMilliseconds;
                timer.Restart();
                for (int i = 0; i < repetitions; i++)
                {
                    double[] projected;
                    double distance;
                    GapCriteria.TryProjectToPlane(new[] { (1.0 + i % 20 * .1) * scale, 2 * scale, 1.005 * scale },
                        origin, normal, 1e-6 * scale, out projected, out distance);
                    int status;
                    uf.Modl.AskPointContainment(projected, top, out status);
                    Check(status == 1, "Timed projection is interior");
                }
                Console.WriteLine((inches ? "Inches" : "Millimeters") + ": " + repetitions + " samples; NX minimum=" +
                    kernelMs + " ms; analytic + face containment=" + timer.ElapsedMilliseconds + " ms.");
            }
            Console.WriteLine("Passed " + checks + " NX projection checks on unsaved synthetic parts.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("NX projection test failed: " + ex.GetType().FullName);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Tag Block(UFSession uf, double[] origin, double[] lengths)
    {
        string[] dimensions = new string[3];
        for (int i = 0; i < 3; i++) dimensions[i] = lengths[i].ToString("R", CultureInfo.InvariantCulture);
        Tag feature, body;
        uf.Modl.CreateBlock1(FeatureSigns.Nullsign, origin, dimensions, out feature);
        uf.Modl.AskFeatBody(feature, out body);
        return body;
    }

    private static double KernelDistance(UFSession uf, Tag face, double[] reference, out double[] point)
    {
        point = new double[3];
        double distance, accuracy;
        uf.Modl.AskMinimumDist3(2, Tag.Null, face, 1, reference, 0, new double[3], out distance, new double[3], point, out accuracy);
        return distance;
    }
}
