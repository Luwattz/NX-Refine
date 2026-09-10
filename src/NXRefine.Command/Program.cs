using System;
using System.Reflection;

namespace NXRefine.Command
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            string assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
            const string prefix = "NXRefine.";
            string command = assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? assemblyName.Substring(prefix.Length)
                : "About";

            NXRefine.Program.Run(ToCommandName(command));
        }

        public static int GetUnloadOption(string dummy)
        {
            return NXRefine.Program.GetUnloadOption(dummy);
        }

        private static string ToCommandName(string command)
        {
            switch (command.ToLowerInvariant())
            {
                case "analyze": return "analyze";
                case "autosimplify": return "simplify";
                case "smallfaces": return "remove-small";
                case "removeblends": return "remove-blends";
                case "fillholes": return "fill-holes";
                case "removemarkings": return "remove-markings";
                case "repairsheets": return "repair-sheets";
                case "patchopenings": return "patch-openings";
                case "settings": return "settings";
                default: return "about";
            }
        }
    }
}
