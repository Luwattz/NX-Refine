using System;
using System.Collections.Generic;
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
        Console.WriteLine("Passed " + count + " gap-criteria checks. NX geometry integration remains manual.");
    }
}
