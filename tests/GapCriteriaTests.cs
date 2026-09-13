using System;
using System.Collections.Generic;
using System.Diagnostics;
using NXRefine.Analysis;

internal static class GapCriteriaTests
{
    private static int count;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        count++;
    }

    public static void Main()
    {
        Check(!GapCriteria.PositiveGap(0, .01, 1e-6, 0), "Shared vertex / zero gap");
        Check(!GapCriteria.PositiveGap(1e-7, .01, 1e-6, 0), "Numerical contact");
        Check(GapCriteria.PositiveGap(.005, .01, 1e-6, 0), "Small positive gap");
        Check(!GapCriteria.PositiveGap(.02, .01, 1e-6, 0), "Over maximum");
        Check(!GapCriteria.PositiveGap(.009, .01, 1e-6, .002), "Uncertain threshold");
        Check(!GapCriteria.PositiveGap(double.NaN, .01, 1e-6, 0), "Invalid measurement");
        Check(GapCriteria.PositiveGap(.005/25.4, .01/25.4, 1e-6/25.4, 0), "Inch part");
        var up = new[] { 0.0, 0.0, 1.0 };
        var down = new[] { 0.0, 0.0, -1.0 };
        Check(GapCriteria.Facing(up, down, up), "Facing across air");
        Check(!GapCriteria.Facing(up, up, up), "Same-facing surfaces");
        Check(!GapCriteria.Facing(down, up, up), "Back-to-back thin material");
        Check(!GapCriteria.Facing(up, down, new[] { 1.0, 0.0, 0.0 }), "Edge-only proximity");
        Check(!GapCriteria.Facing(up, down, new double[3]), "Coincident points");
        var samples = new List<double[]>();
        samples.Add(new[] { 0.0, 0.0, 0.0 });
        samples.Add(new[] { 0.0, 1.0, 0.0 });
        samples.Add(new[] { 0.0, 2.0, 0.0 });
        Check(!GapCriteria.NewSampleFormsArea(samples, .25), "Collinear overlap rejected");
        samples.Add(new[] { 1.0, 0.0, 0.0 });
        Check(GapCriteria.NewSampleFormsArea(samples, .25), "Patch accepted after four samples instead of eighteen");
        // Verify the streaming acceptance against an independent exhaustive
        // triangle determinant on planar point sets, including degeneracies.
        var random = new Random(37);
        for (int trial = 0; trial < 100; trial++)
        {
            samples.Clear();
            bool streaming = false, exhaustive = false;
            for (int n = 0; n < 18; n++)
            {
                samples.Add(new[] { (double)random.Next(3), (double)(trial % 2 == 0 ? 0 : random.Next(3)), 0.0 });
                streaming |= GapCriteria.NewSampleFormsArea(samples, .25);
            }
            for (int a = 0; a < samples.Count; a++)
                for (int b = a + 1; b < samples.Count; b++)
                    for (int c = b + 1; c < samples.Count; c++)
                    {
                        double[] p = samples[a], q = samples[b], r = samples[c];
                        exhaustive |= Math.Abs(p[0]*(q[1]-r[1])+q[0]*(r[1]-p[1])+r[0]*(p[1]-q[1])) > .25;
                    }
            Check(streaming == exhaustive, "Streaming/exhaustive equivalence " + trial);
        }
        var boxes = new double[1000][];
        for (int axis = 0; axis < 3; axis++)
        {
            for (int n = 0; n < boxes.Length; n++)
            {
                boxes[n] = new[] { 0.0, 0.0, 0.0, 10.0, 10.0, 10.0 };
                boxes[n][axis] = n * 2;
                boxes[n][axis + 3] = n * 2 + 1;
            }
            Check(GapCriteria.SweepAxis(boxes, .2) == axis, "Sparse sweep axis " + axis);
        }
        CheckSpatialIndex();
        CheckPlaneFilter();
        CheckPlanarProjection();
        CheckMinimumExclusion();
        CheckPointQueryCache();
        CheckSamplePlaneBound();
        BenchmarkSpatialIndex();
        Console.WriteLine("Passed " + count + " gap-criteria checks. NX geometry integration remains manual.");
    }

    private static bool ExhaustiveBoxesWithin(double[] a, double[] b, double tolerance)
    {
        double distanceSquared = 0;
        for (int k = 0; k < 3; k++)
        {
            double gap = 0;
            if (a[k + 3] < b[k]) gap = b[k] - a[k + 3];
            else if (b[k + 3] < a[k]) gap = a[k] - b[k + 3];
            distanceSquared += gap * gap;
        }
        return distanceSquared <= tolerance * tolerance;
    }

    private static void CompareIndex(double[][] boxes, double tolerance)
    {
        var index = new FaceBoxIndex(boxes);
        var matches = new List<int>();
        for (int i = 0; i < boxes.Length; i++)
        {
            index.FindLater(i, tolerance, matches);
            int next = 0;
            for (int j = i + 1; j < boxes.Length; j++)
                if (ExhaustiveBoxesWithin(boxes[i], boxes[j], tolerance))
                {
                    Check(next < matches.Count && matches[next] == j, "Spatial index preserves exhaustive pairs and order");
                    next++;
                }
            Check(next == matches.Count, "Spatial index has no extra pairs");
        }
    }

    private static void CheckSpatialIndex()
    {
        CompareIndex(new double[0][], .01);
        CompareIndex(new[] { new double[6] }, 0);
        var random = new Random(913);
        for (int trial = 0; trial < 20; trial++)
        {
            var boxes = new double[180][];
            for (int i = 0; i < boxes.Length; i++)
            {
                boxes[i] = new double[6];
                for (int k = 0; k < 3; k++)
                {
                    boxes[i][k] = random.Next(20) - 10;
                    boxes[i][k + 3] = boxes[i][k] + (trial % 3 == 0 ? 0 : random.Next(8));
                }
            }
            // A large carrier spanning many otherwise independent faces.
            boxes[0] = new[] { -100.0, -100.0, -100.0, 100.0, 100.0, 100.0 };
            CompareIndex(boxes, trial % 4 == 0 ? 0 : .01 * trial);
        }
        var identical = new double[40][];
        for (int i = 0; i < identical.Length; i++) identical[i] = new[] { 0.0, 0.0, 0.0, 1.0, 1.0, 1.0 };
        CompareIndex(identical, .01);
        CompareIndex(new[] {
            new[] { 0.0, 0.0, 0.0, 1.0, 1.0, 0.0 },
            new[] { 0.0, 0.0, .01, 1.0, 1.0, .01 },
            new[] { 1.006, 1.008, 0.0, 2.0, 2.0, 0.0 },
            new[] { 0.0, 0.0, .01000001, 1.0, 1.0, .01000001 }
        }, .01);
    }

    private static void CheckPlaneFilter()
    {
        var origin = new double[3];
        var up = new[] { 0.0, 0.0, 1.0 };
        Check(GapCriteria.PlaneMayFaceBox(origin, up, new[] { -1.0, -1.0, .005, 1.0, 1.0, .005 }, .01, 1e-6), "Plane allows open gap");
        Check(!GapCriteria.PlaneMayFaceBox(origin, up, new[] { -1.0, -1.0, -.005, 1.0, 1.0, -.005 }, .01, 1e-6), "Plane rejects back-to-back material");
        Check(!GapCriteria.PlaneMayFaceBox(origin, up, new[] { -1.0, -1.0, 0.0, 1.0, 1.0, 0.0 }, .01, 1e-6), "Plane rejects coplanar contacts");
        Check(!GapCriteria.PlaneMayFaceBox(origin, up, new[] { -1.0, -1.0, .02, 1.0, 1.0, .02 }, .01, 1e-6), "Plane rejects excessive distance");
        Check(GapCriteria.PlaneMayFaceBox(origin, up, new[] { -1.0, -1.0, -.02, 1.0, 1.0, .005 }, .01, 1e-6), "Tilted partial gap crossing plane survives");
        Check(GapCriteria.PlaneMayFaceBox(null, up, new double[6], .01, 1e-6), "Unknown plane falls through");
        var random = new Random(291);
        // Construct actual admissible sample displacements on arbitrarily
        // oriented planes, then enclose the other sample in a random box.
        for (int i = 0; i < 4000; i++)
        {
            double units = i % 2 == 0 ? 1 : 1 / 25.4;
            double maximum = .01 * units, resolution = 1e-6 * units;
            double[] n = { random.NextDouble() - .5, random.NextDouble() - .5, random.NextDouble() - .5 };
            double length = GapCriteria.Length(n);
            for (int k = 0; k < 3; k++) n[k] /= length;
            double d = i % 5 == 0 ? maximum : resolution * 1.2 + random.NextDouble() * (maximum - resolution * 1.2);
            double[] displacement = { n[0] * d, n[1] * d, n[2] * d };
            var down = new[] { -n[0], -n[1], -n[2] };
            Check(GapCriteria.Facing(n, down, displacement), "Generated facing witness");
            double[] planeOrigin = { random.NextDouble() * 1e5, random.NextDouble() * 1e5, random.NextDouble() * 1e5 };
            double[] box = new double[6];
            for (int k = 0; k < 3; k++)
            {
                double q = planeOrigin[k] + displacement[k];
                box[k] = q - random.NextDouble() * units;
                box[k + 3] = q + random.NextDouble() * units;
            }
            Check(GapCriteria.PlaneMayFaceBox(planeOrigin, n, box, maximum, resolution), "Plane filter preserves facing witness");
        }
    }

    private static void BenchmarkSpatialIndex()
    {
        const int side = 32;
        var boxes = new double[side * side * side][];
        int next = 0;
        for (int x = 0; x < side; x++)
            for (int y = 0; y < side; y++)
                for (int z = 0; z < side; z++)
                    boxes[next++] = new[] { x * 2.0, y * 2.0, z * 2.0, x * 2.0 + 1, y * 2.0 + 1, z * 2.0 + 1 };
        var elapsed = Stopwatch.StartNew();
        long sweepChecks = 0;
        for (int i = 0; i < boxes.Length; i++)
            for (int j = i + 1; j < boxes.Length && boxes[j][0] <= boxes[i][3] + .01; j++)
            {
                sweepChecks++;
                if (ExhaustiveBoxesWithin(boxes[i], boxes[j], .01)) throw new Exception("Unexpected benchmark pair");
            }
        long sweepMs = elapsed.ElapsedMilliseconds;
        elapsed.Restart();
        var index = new FaceBoxIndex(boxes);
        var matches = new List<int>();
        for (int i = 0; i < boxes.Length; i++)
        {
            index.FindLater(i, .01, matches);
            Check(matches.Count == 0, "Sparse benchmark has no candidates");
        }
        Check(index.BoxTests < sweepChecks / 4, "Spatial index removes at least 75% of sparse broad-phase work");
        Console.WriteLine("Synthetic " + boxes.Length + "-box benchmark: sweep " + sweepChecks + " tests / " + sweepMs +
            " ms; index " + index.BoxTests + " tests / " + elapsed.ElapsedMilliseconds + " ms (including build). No NX kernel timing.");
    }

    private static void CheckMinimumExclusion()
    {
        Check(GapCriteria.MinimumExcludesGap(.02, 0, .01, 1e-6), "Definite excess skips partial search");
        Check(!GapCriteria.MinimumExcludesGap(0, 0, .01, 1e-6), "Joined edge still needs partial search");
        Check(!GapCriteria.MinimumExcludesGap(.005, 0, .01, 1e-6), "Positive minimum still needs patch verification");
        Check(!GapCriteria.MinimumExcludesGap(.011, .002, .01, 1e-6), "Uncertainty overlaps maximum");
        Check(!GapCriteria.MinimumExcludesGap(.01, 0, .01, 1e-6), "Maximum is inclusive");
        Check(!GapCriteria.MinimumExcludesGap(.01000001, 0, .01, 1e-6), "Boundary roundoff falls through");
        Check(!GapCriteria.MinimumExcludesGap(double.NaN, 0, .01, 1e-6), "Failed minimum is not a rejection");
        Check(!GapCriteria.MinimumExcludesGap(.02, double.PositiveInfinity, .01, 1e-6), "Unknown accuracy is not a rejection");
        Check(!GapCriteria.MinimumExcludesGap(.02, -1, .01, 1e-6), "Negative accuracy is not a rejection");
        Check(GapCriteria.MinimumExcludesGap(.02 / 25.4, 0, .01 / 25.4, 1e-6 / 25.4), "Inch excess skips partial search");
        // A reported minimum whose uncertainty contains an in-range true
        // minimum must never suppress the existing partial-gap fallback.
        var random = new Random(384);
        for (int i = 0; i < 2000; i++)
        {
            double actual = random.NextDouble() * .01;
            double error = random.NextDouble() * .02;
            double measured = actual + random.NextDouble() * error;
            Check(!GapCriteria.MinimumExcludesGap(measured, error, .01, 1e-6), "Uncertain valid minimum survives");
        }
    }

    private static void CheckPointQueryCache()
    {
        var cache = new PointQueryCache<int, int>(3);
        int result;
        var p = new[] { 1.0, 2.0, 3.0 };
        Check(!cache.TryGet(1, p, out result), "New point needs a kernel query");
        cache.Store(1, p, 2);
        Check(cache.TryGet(1, new[] { 1.0, 2.0, 3.0 }, out result) && result == 2, "Equal coordinates reuse result");
        Check(!cache.TryGet(2, p, out result), "Different body/face cannot share a result");
        Check(!cache.TryGet(1, new[] { 1.0, 2.0, 3.0 + 1e-12 }, out result), "Sub-resolution points stay distinct");
        p[0] = 7;
        Check(cache.TryGet(1, new[] { 1.0, 2.0, 3.0 }, out result), "Input array mutation cannot move cached key");
        Check(!cache.TryGet(1, p, out result), "Mutated coordinate is a different query");
        cache.Store(1, new double[3], 1);
        Check(cache.TryGet(1, new[] { -0.0, 0.0, 0.0 }, out result) && result == 1, "Signed zero uses consistent equality/hash");
        cache.Store(2, new double[3], 3);
        cache.Store(3, new double[3], 2);
        Check(cache.Count == 3 && !cache.TryGet(3, new double[3], out result), "Capacity does not admit more entries");
        cache.Store(1, new double[3], 3);
        Check(cache.TryGet(1, new double[3], out result) && result == 3, "Existing key can be refreshed at capacity");
        cache.Clear();
        Check(cache.Count == 0 && !cache.TryGet(1, new double[3], out result), "Geometry invalidation removes stale material state");
        cache.Store(1, new double[3], 2);
        Check(cache.TryGet(1, new double[3], out result) && result == 2, "New geometry can change material to empty space");
        cache.Store(1, new[] { double.NaN, 0.0, 0.0 }, 1);
        cache.Store(1, new[] { 0.0, double.PositiveInfinity, 0.0 }, 1);
        Check(cache.Count == 1, "Invalid coordinate results never retained");
        Check(!cache.TryGet(1, new[] { double.NaN, 0.0, 0.0 }, out result), "NaN query cannot hit");
        var disabled = new PointQueryCache<int, int>(0);
        disabled.Store(1, new double[3], 2);
        Check(!disabled.TryGet(1, new double[3], out result), "Zero-capacity cache preserves uncached execution");

        // The kernel substitute changes on a geometry revision. Across gap
        // edits only classifications are reused; threshold decisions rerun.
        var sequence = new PointQueryCache<int, double>(100);
        int queries = 0, total = 0;
        for (int revision = 0; revision < 2; revision++)
        {
            sequence.Clear();
            foreach (double maximum in new[] { .01, .005, .02 })
                for (int i = 0; i < 100; i++)
                {
                    double[] query = { i * .001, 0, 0 };
                    double expected = i * .0001 + revision * .004, measured;
                    total++;
                    if (!sequence.TryGet(1, query, out measured))
                    {
                        queries++;
                        measured = expected;
                        sequence.Store(1, query, measured);
                    }
                    Check(GapCriteria.PositiveGap(measured, maximum, 1e-6, 0) ==
                        GapCriteria.PositiveGap(expected, maximum, 1e-6, 0), "Cached/uncached tolerance result equivalence");
                }
        }
        Check(queries == 200, "Each point queried once per geometry revision");
        Console.WriteLine("Synthetic query reuse: " + total + " requests, " + queries + " evaluations; tolerance decisions unchanged. No NX timing.");
    }

    private static void CheckSamplePlaneBound()
    {
        var up = new[] { 0.0, 0.0, 1.0 };
        var origin = new double[3];
        Check(!GapCriteria.PointMayReachPlane(new[] { 0.0, 0.0, .02 }, origin, up, .01, 1e-6), "Far sample skips target projection");
        Check(!GapCriteria.PointMayReachPlane(new[] { 0.0, 0.0, -.02 }, origin, up, .01, 1e-6), "Plane bound works on both sides");
        Check(GapCriteria.PointMayReachPlane(new[] { 100.0, 100.0, .005 }, origin, up, .01, 1e-6), "Tangential offsets require trimmed-face query");
        Check(GapCriteria.PointMayReachPlane(new[] { 0.0, 0.0, .01 }, origin, up, .01, 1e-6), "Threshold sample preserved");
        Check(GapCriteria.PointMayReachPlane(new[] { 0.0, 0.0, .01000001 }, origin, up, .01, 1e-6), "Uncertain plane boundary preserved");
        Check(GapCriteria.PointMayReachPlane(origin, null, up, .01, 1e-6), "Unknown supporting plane falls through");
        Check(GapCriteria.PointMayReachPlane(origin, origin, new double[3], .01, 1e-6), "Degenerate supporting plane falls through");
        var random = new Random(178);
        for (int i = 0; i < 2000; i++)
        {
            double scale = i % 2 == 0 ? 1 : 1 / 25.4;
            double[] n = { random.NextDouble() - .5, random.NextDouble() - .5, random.NextDouble() - .5 };
            double length = GapCriteria.Length(n);
            for (int k = 0; k < 3; k++) n[k] /= length;
            double[] tangent = { n[1], -n[0], 0 };
            double[] planeOrigin = { 12 * scale, -7 * scale, 25 * scale };
            double[] query = new double[3];
            double signedDistance = (random.NextDouble() * 2 - 1) * .01 * scale;
            for (int k = 0; k < 3; k++) query[k] = planeOrigin[k] + tangent[k] * 3 * scale + n[k] * signedDistance;
            Check(GapCriteria.PointMayReachPlane(query, planeOrigin, n, .01 * scale, 1e-6 * scale), "Rotated valid sample survives plane bound");
        }
    }

    private static void CheckPlanarProjection()
    {
        double[] point;
        double distance;
        var zero = new double[3];
        var up = new[] { 0.0, 0.0, 1.0 };
        Check(GapCriteria.TryProjectToPlane(new[] { 2.0, 3.0, .005 }, zero, up, 1e-6, out point, out distance) &&
            point[0] == 2 && point[1] == 3 && point[2] == 0 && distance == .005, "Interior planar projection");
        Check(GapCriteria.TryProjectToPlane(new[] { 2.0, 3.0, -.005 }, zero, new[] { 0.0, 0.0, -7.0 }, 1e-6, out point, out distance) &&
            point[2] == 0 && distance == .005, "Signed and non-unit normals");
        Check(!GapCriteria.TryProjectToPlane(zero, null, up, 1e-6, out point, out distance), "Unknown plane uses kernel");
        Check(!GapCriteria.TryProjectToPlane(zero, zero, zero, 1e-6, out point, out distance), "Invalid normal uses kernel");
        Check(!GapCriteria.TryProjectToPlane(new[] { double.NaN, 0.0, 0.0 }, zero, up, 1e-6, out point, out distance), "Invalid reference uses kernel");
        Check(!GapCriteria.TryProjectToPlane(new[] { 1e12, 0.0, 0.0 }, new[] { 1e12, 0.0, 0.0 }, up,
            1e-6 / 25.4, out point, out distance), "Large coordinates with tiny resolution use kernel");

        var random = new Random(936);
        for (int i = 0; i < 3000; i++)
        {
            double units = i % 2 == 0 ? 1.0 : 1.0 / 25.4;
            double a = random.NextDouble() * Math.PI * 2, b = random.NextDouble() * Math.PI * 2;
            // An independently constructed orthonormal basis for a rotated
            // plane. The known foot is a combination of its two tangent axes.
            double[] n = { Math.Cos(a) * Math.Cos(b), Math.Sin(a) * Math.Cos(b), Math.Sin(b) };
            double[] u = { -Math.Sin(a), Math.Cos(a), 0 };
            double[] v = { -Math.Cos(a) * Math.Sin(b), -Math.Sin(a) * Math.Sin(b), Math.Cos(b) };
            double[] origin = { 11 * units, -7 * units, 24 * units };
            double x = (random.NextDouble() - .5) * units, y = (random.NextDouble() - .5) * units;
            double d = (random.NextDouble() - .5) * .02 * units;
            double[] foot = new double[3], reference = new double[3], scaled = new double[3];
            double scale = i % 3 == 0 ? -5 : 2;
            for (int k = 0; k < 3; k++)
            {
                foot[k] = origin[k] + x * u[k] + y * v[k];
                reference[k] = foot[k] + d * n[k];
                scaled[k] = n[k] * scale;
            }
            Check(GapCriteria.TryProjectToPlane(reference, origin, scaled, 1e-6 * units, out point, out distance), "Rotated plane projects");
            for (int k = 0; k < 3; k++) Check(Math.Abs(point[k] - foot[k]) < 1e-11 * units, "Projection matches known foot");
            Check(Math.Abs(distance - Math.Abs(d)) < 1e-11 * units, "Projection matches known distance");
        }
    }
}
