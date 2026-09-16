using NXOpen;

public static class RunNxMarkingsModelRegression
{
    public static void Main(string[] args)
    {
        // Let NX select the compiled application's runtime. Loading a Framework
        // DLL through Assembly.LoadFrom in a .NET 8 journal mixes NX wrappers.
        Session.GetSession().ExecuteWithStringArguments(args[0], "Main",
            new[] { args[1], args[2], args[3] });
    }
}
