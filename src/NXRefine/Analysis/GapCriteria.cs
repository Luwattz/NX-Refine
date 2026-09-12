using System;
using System.Collections.Generic;

namespace NXRefine.Analysis
{
    // Numerical policy shared by the NX adapter and independent regression tests.
    internal static class GapCriteria
    {
        // Choose the sweep direction with the fewest overlapping intervals.
        // This changes pair enumeration order, not the distance predicate.
        public static int SweepAxis(double[][] boxes, double tolerance)
        {
            int bestAxis = 0;
            long bestCount = long.MaxValue;
            for (int axis = 0; axis < 3; axis++)
            {
                double[] starts = new double[boxes.Length];
                for (int i = 0; i < boxes.Length; i++) starts[i] = boxes[i][axis];
                Array.Sort(starts);
                long count = 0;
                foreach (double[] box in boxes)
                {
                    int low = 0, high = starts.Length;
                    double end = box[axis + 3] + tolerance;
                    while (low < high)
                    {
                        int middle = low + (high - low) / 2;
                        if (starts[middle] <= end) low = middle + 1;
                        else high = middle;
                    }
                    count += low;
                }
                if (count < bestCount) { bestCount = count; bestAxis = axis; }
            }
            return bestAxis;
        }

        // Called after appending each accepted sample. Every triple is checked
        // exactly once, so acceptance matches the former exhaustive final pass.
        public static bool NewSampleFormsArea(IList<double[]> samples, double minimumCrossLength)
        {
            int last = samples.Count - 1;
            if (last < 2) return false;
            double[] p = samples[last];
            for (int i = 0; i < last; i++)
                for (int j = i + 1; j < last; j++)
                {
                    double ax = samples[j][0] - samples[i][0], ay = samples[j][1] - samples[i][1], az = samples[j][2] - samples[i][2];
                    double bx = p[0] - samples[i][0], by = p[1] - samples[i][1], bz = p[2] - samples[i][2];
                    double x = ay*bz-az*by, y = az*bx-ax*bz, z = ax*by-ay*bx;
                    if (Math.Sqrt(x*x+y*y+z*z) > minimumCrossLength) return true;
                }
            return false;
        }

        public static bool PositiveGap(double distance, double maximum, double resolution, double accuracy)
        {
            return Finite(distance) && Finite(maximum) && Finite(resolution) && Finite(accuracy) &&
                resolution > 0 && accuracy >= 0 && maximum > resolution &&
                distance - accuracy > resolution && distance + accuracy <= maximum;
        }

        public static bool Facing(double[] firstNormal, double[] secondNormal, double[] displacement)
        {
            double a = Length(firstNormal), b = Length(secondNormal), d = Length(displacement);
            if (!Finite(a) || !Finite(b) || !Finite(d) || a < 1e-12 || b < 1e-12 || d < 1e-12) return false;
            // Signed outward normals must face each other across the gap.
            return Dot(firstNormal, secondNormal) / (a * b) <= -0.95 &&
                Dot(firstNormal, displacement) / (a * d) >= 0.95 &&
                Dot(secondNormal, displacement) / (b * d) <= -0.95;
        }

        public static double Dot(double[] a, double[] b) { return a[0]*b[0] + a[1]*b[1] + a[2]*b[2]; }
        public static double Length(double[] a) { return Math.Sqrt(Dot(a, a)); }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
