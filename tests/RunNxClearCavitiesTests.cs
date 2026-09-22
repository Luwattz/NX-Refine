using System;
using NXOpen;

public static class RunNxClearCavitiesTests
{
    public static void Main(string[] args)
    {
        string[] values = new string[args.Length - 1];
        Array.Copy(args, 1, values, 0, values.Length);
        // NX selects the Framework runtime for the compiled regression DLL.
        Session.GetSession().ExecuteWithStringArguments(args[0], "Main", values);
    }
}
