using System;
using System.Collections.Generic;

namespace NXRefine.Analysis
{
    // Numerical policy shared by the NX adapter and independent regression tests.
    internal static class GapCriteria
    {
        // Distance to the infinite supporting plane is a lower bound on the
        // distance to any point of its trimmed face. This rejects only sample
        // points that cannot possibly reach that face within the current gap.
        public static bool PointMayReachPlane(double[] reference, double[] origin, double[] normal,
            double maximum, double resolution)
        {
            if (origin == null || normal == null || !Finite(maximum) || maximum < 0 ||
                !Finite(resolution) || resolution <= 0) return true;
            double length = Length(normal);
            if (!Finite(length) || length < 1e-12) return true;
            double signed = 0, magnitude = 0;
            for (int k = 0; k < 3; k++)
            {
                if (!Finite(reference[k]) || !Finite(origin[k])) return true;
                signed += (reference[k] - origin[k]) * (normal[k] / length);
                magnitude += Math.Abs(reference[k]) + Math.Abs(origin[k]);
            }
            double slack = resolution * .25 + magnitude * 1e-14;
            return !Finite(signed) || Math.Abs(signed) <= maximum + slack;
        }

        // An analytic projection is a closest point only if it lies inside the
        // TRIMMED face. The NX adapter must check that before using this result.
        public static bool TryProjectToPlane(double[] reference, double[] origin, double[] normal,
            double resolution, out double[] point, out double distance)
        {
            point = null;
            distance = 0;
            if (origin == null || normal == null || !Finite(resolution) || resolution <= 0) return false;
            double length = Length(normal);
            if (!Finite(length) || length < 1e-12) return false;
            double signed = 0, magnitude = 0;
            for (int k = 0; k < 3; k++)
            {
                if (!Finite(reference[k]) || !Finite(origin[k])) return false;
                signed += (reference[k] - origin[k]) * (normal[k] / length);
                magnitude += Math.Abs(reference[k]) + Math.Abs(origin[k]);
            }
            // Use the existing kernel path when roundoff could consume the
            // sample accuracy budget (e.g. tiny inch gaps far from the origin).
            if (!Finite(signed) || magnitude * 1e-14 > resolution * .1) return false;
            point = new double[3];
            double squared = 0;
            for (int k = 0; k < 3; k++)
            {
                point[k] = reference[k] - signed * (normal[k] / length);
                double delta = reference[k] - point[k];
                squared += delta * delta;
            }
            distance = Math.Sqrt(squared);
            return Finite(distance);
        }

        // Reject only a lower bound strictly above the limit. A zero minimum,
        // ambiguous threshold, failed measurement or rejected local patch can
        // still have a qualifying partial opening elsewhere on the face.
        public static bool MinimumExcludesGap(double distance, double accuracy, double maximum, double resolution)
        {
            return Finite(distance) && distance >= 0 && Finite(accuracy) && accuracy >= 0 &&
                Finite(maximum) && maximum >= 0 && Finite(resolution) && resolution > 0 &&
                distance - accuracy > maximum + resolution * .1;
        }

        public static bool BoxesWithin(double[] first, double[] second, double tolerance)
        {
            double squared = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                double gap = Math.Max(0, Math.Max(second[axis] - first[axis + 3], first[axis] - second[axis + 3]));
                squared += gap * gap;
            }
            return squared <= tolerance * tolerance;
        }

        // A point on the other face must lie in front of this outward plane,
        // within the search distance. Project the entire other box, so tilted
        // planes and partially attached faces are not rejected by their centers.
        public static bool PlaneMayFaceBox(double[] origin, double[] normal, double[] box,
            double maximum, double resolution)
        {
            if (origin == null || normal == null) return true;
            double length = Length(normal);
            if (!Finite(length) || length < 1e-12) return true;
            double low = 0, high = 0, magnitude = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                double n = normal[axis] / length;
                double a = (box[axis] - origin[axis]) * n;
                double b = (box[axis + 3] - origin[axis]) * n;
                low += Math.Min(a, b);
                high += Math.Max(a, b);
                magnitude += Math.Abs(origin[axis]) + Math.Abs(box[axis]) + Math.Abs(box[axis + 3]);
            }
            // Leave uncertain boundary cases to the kernel, including roundoff
            // on parts far from the origin. Facing requires dot(n, d) >= .95|d|.
            double slack = resolution * .25 + magnitude * 1e-14;
            return !(high < .95 * resolution - slack || low > maximum + slack);
        }

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
