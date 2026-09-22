using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NXOpen;
using NXRefine.Core;

namespace NXRefine.Analysis
{
    // NX 2512 UGOPEN/uf_brep_types.h specifies that the first child of a
    // solid is its outer shell; subsequent shells bound internal voids.
    // Read the kernel's topology once instead of reconstructing it across
    // thousands of managed edge calls and sampling exterior visibility.
    internal static class CavityShellDetector
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Topology
        {
            public int Type;
            public Tag Tag;
            public int ChildCount;
            public IntPtr Children;
            public IntPtr UserData; // UF_BREP_u: pointer-sized union
            public IntPtr Extension;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Child
        {
            public IntPtr Topology;
            public int Orientation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Options
        {
            public int Count;
            public IntPtr Tokens;
            public IntPtr Data;
        }

        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int UF_BREP_ask_topology(Tag body, ref Options options,
            out IntPtr topology, out int stateCount, IntPtr states);

        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int UF_BREP_release_topology(IntPtr topology, IntPtr freeFunction);

        private static readonly int ChildSize = Marshal.SizeOf(typeof(Child));

        internal static Face[][] Find(Body body)
        {
            SelectionScan.Checkpoint();
            if (body == null || body.IsOccurrence || !body.IsSolidBody)
                return new Face[0][];
            var faces = new Dictionary<Tag, Face>();
            foreach (Face face in body.GetFaces()) faces.Add(face.Tag, face);
            IntPtr topology = IntPtr.Zero;
            try
            {
                var options = new Options();
                int states;
                // A null states pointer requests no allocated diagnostics array.
                int error = UF_BREP_ask_topology(body.Tag, ref options, out topology, out states, IntPtr.Zero);
                if (error != 0 || states != 0 || topology == IntPtr.Zero)
                    throw new InvalidOperationException("Cannot safely read cavity topology (NX error " + error +
                        ", topology states " + states + "). No faces were deleted.");
                SelectionScan.Checkpoint();
                Topology root = Read(topology);
                if (root.Type != 1 || root.Tag != body.Tag || root.ChildCount < 1)
                    throw InvalidTopology();
                var seen = new HashSet<Tag>();
                var groups = new List<Face[]>();
                for (int i = 0; i < root.ChildCount; i++)
                {
                    SelectionScan.Checkpoint();
                    Topology shell = ReadChild(root, i);
                    if (shell.Type != 4 || shell.ChildCount < 1) throw InvalidTopology();
                    var group = new Face[shell.ChildCount];
                    for (int j = 0; j < shell.ChildCount; j++)
                    {
                        SelectionScan.Checkpoint();
                        Topology face = ReadChild(shell, j);
                        Face resolved;
                        if (face.Type != 5 || !faces.TryGetValue(face.Tag, out resolved) || !seen.Add(face.Tag))
                            throw InvalidTopology();
                        group[j] = resolved;
                    }
                    if (i > 0) groups.Add(group);
                }
                if (seen.Count != faces.Count) throw InvalidTopology();
                return groups.ToArray();
            }
            finally
            {
                if (topology != IntPtr.Zero)
                {
                    int error = UF_BREP_release_topology(topology, IntPtr.Zero);
                    if (error != 0) throw new InvalidOperationException("NX could not release cavity topology: " + error);
                }
            }
        }

        private static Topology Read(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) throw InvalidTopology();
            return (Topology)Marshal.PtrToStructure(pointer, typeof(Topology));
        }

        private static Topology ReadChild(Topology parent, int index)
        {
            if (parent.Children == IntPtr.Zero || index < 0 || index >= parent.ChildCount)
                throw InvalidTopology();
            Child child = (Child)Marshal.PtrToStructure(
                IntPtr.Add(parent.Children, checked(index * ChildSize)), typeof(Child));
            return Read(child.Topology);
        }

        private static Exception InvalidTopology()
        {
            return new InvalidOperationException("Unexpected NX shell topology. No cavity faces were deleted.");
        }
    }
}
