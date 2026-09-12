using System;

namespace NXRefine.Analysis
{
    // Numerical policy shared by the NX adapter and independent regression tests.
    internal static class GapCriteria
    {
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
