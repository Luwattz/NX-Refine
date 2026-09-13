using System;
using System.Reflection;

// Journal entry point lets run_journal initialize the NX environment before
// loading the compiled kernel test and its production numerical helpers.
public static class RunNxGapProjectionJournal
{
    public static void Main(string[] args)
    {
        Assembly tests = Assembly.LoadFrom(args[0]);
        int result = (int)tests.GetType("NxGapProjectionTests").GetMethod("Main", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, new object[] { new string[0] });
        if (result != 0) throw new InvalidOperationException("NX gap projection checks failed; see the process output.");
    }
}
