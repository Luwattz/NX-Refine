using System;
using System.Reflection;

public static class RunNxRemoveBlendsBench
{
    public static void Main(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("usage: runner <bench.exe> <part> <NXRefine.dll> [minimumRemoved]");
        Assembly tests = Assembly.LoadFrom(args[0]);
        string[] testArgs = new string[args.Length - 1];
        Array.Copy(args, 1, testArgs, 0, testArgs.Length);
        int result = (int)tests.GetType("NxRemoveBlendsBench").GetMethod("Main", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, new object[] { testArgs });
        if (result != 0) throw new InvalidOperationException("Remove Blends benchmark failed.");
    }
}
