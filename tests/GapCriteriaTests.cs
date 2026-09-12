using System;
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
        Console.WriteLine("Passed " + count + " gap-criteria checks. NX geometry integration remains manual.");
    }
}
