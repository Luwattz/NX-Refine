using System;
using System.Collections.Generic;

namespace NXRefine.Analysis
{
    // Immutable, scan-local bounding volume hierarchy. Leaves refer to the
    // caller's face order so queries can preserve pair orientation and order.
    internal sealed class FaceBoxIndex
    {
        private readonly double[][] boxes;
        private readonly int[] order;
        private readonly Node root;
        public long BoxTests { get; private set; }

        public FaceBoxIndex(double[][] boxes)
        {
            this.boxes = boxes;
            order = new int[boxes.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            if (order.Length != 0) root = Build(0, order.Length);
        }

        public void FindLater(int first, double tolerance, List<int> matches)
        {
            matches.Clear();
            if (root != null) Query(root, first, tolerance, matches);
            matches.Sort();
        }

        private Node Build(int start, int count)
        {
            var node = new Node { Start = start, Count = count, Box = (double[])boxes[order[start]].Clone() };
            double[] centerMin = { double.MaxValue, double.MaxValue, double.MaxValue };
            double[] centerMax = { double.MinValue, double.MinValue, double.MinValue };
            for (int i = start; i < start + count; i++)
            {
                int index = order[i];
                node.MaxIndex = Math.Max(node.MaxIndex, index);
                for (int axis = 0; axis < 3; axis++)
                {
                    node.Box[axis] = Math.Min(node.Box[axis], boxes[index][axis]);
                    node.Box[axis + 3] = Math.Max(node.Box[axis + 3], boxes[index][axis + 3]);
                    double center = boxes[index][axis] * .5 + boxes[index][axis + 3] * .5;
                    centerMin[axis] = Math.Min(centerMin[axis], center);
                    centerMax[axis] = Math.Max(centerMax[axis], center);
                }
            }
            if (count <= 8) return node;
            int splitAxis = 0;
            for (int axis = 1; axis < 3; axis++)
                if (centerMax[axis] - centerMin[axis] > centerMax[splitAxis] - centerMin[splitAxis]) splitAxis = axis;
            Array.Sort(order, start, count, new CenterComparer(boxes, splitAxis));
            int leftCount = count / 2;
            node.Left = Build(start, leftCount);
            node.Right = Build(start + leftCount, count - leftCount);
            return node;
        }

        private void Query(Node node, int first, double tolerance, List<int> matches)
        {
            if (node.MaxIndex <= first) return;
            BoxTests++;
            if (!GapCriteria.BoxesWithin(boxes[first], node.Box, tolerance)) return;
            if (node.Left != null)
            {
                Query(node.Left, first, tolerance, matches);
                Query(node.Right, first, tolerance, matches);
                return;
            }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                int second = order[i];
                if (second <= first) continue;
                BoxTests++;
                if (GapCriteria.BoxesWithin(boxes[first], boxes[second], tolerance)) matches.Add(second);
            }
        }

        private sealed class CenterComparer : IComparer<int>
        {
            private readonly double[][] boxes;
            private readonly int axis;
            public CenterComparer(double[][] boxes, int axis) { this.boxes = boxes; this.axis = axis; }
            public int Compare(int a, int b)
            {
                double ca = boxes[a][axis] * .5 + boxes[a][axis + 3] * .5;
                double cb = boxes[b][axis] * .5 + boxes[b][axis + 3] * .5;
                int compare = ca.CompareTo(cb);
                return compare != 0 ? compare : a.CompareTo(b);
            }
        }

        private sealed class Node
        {
            public double[] Box;
            public int Start, Count, MaxIndex;
            public Node Left, Right;
        }
    }
}
