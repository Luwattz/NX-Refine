using System;
using System.Collections.Generic;

namespace NXRefine.Analysis
{
    // Exact-coordinate memoization for one geometry snapshot. No rounding or
    // tolerance buckets: two points on opposite sides of a tiny gap must not
    // share a result. Once full, new queries continue uncached.
    internal sealed class PointQueryCache<TEntity, TResult>
    {
        private readonly int capacity;
        private readonly Dictionary<Key, TResult> entries = new Dictionary<Key, TResult>();
        public int Count { get { return entries.Count; } }

        public PointQueryCache(int capacity)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException("capacity");
            this.capacity = capacity;
        }

        public bool TryGet(TEntity entity, double[] point, out TResult result)
        {
            result = default(TResult);
            return Valid(point) && entries.TryGetValue(new Key(entity, point), out result);
        }

        public void Store(TEntity entity, double[] point, TResult result)
        {
            if (!Valid(point)) return;
            var key = new Key(entity, point);
            if (entries.Count < capacity || entries.ContainsKey(key)) entries[key] = result;
        }

        public void Clear() { entries.Clear(); }

        private static bool Valid(double[] point)
        {
            if (point == null || point.Length != 3) return false;
            for (int i = 0; i < 3; i++)
                if (double.IsNaN(point[i]) || double.IsInfinity(point[i])) return false;
            return true;
        }

        private struct Key : IEquatable<Key>
        {
            private readonly TEntity entity;
            private readonly double x, y, z;
            public Key(TEntity entity, double[] point)
            {
                this.entity = entity;
                x = point[0]; y = point[1]; z = point[2];
            }
            public bool Equals(Key other)
            {
                return EqualityComparer<TEntity>.Default.Equals(entity, other.entity) &&
                    x == other.x && y == other.y && z == other.z;
            }
            public override bool Equals(object other) { return other is Key && Equals((Key)other); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = EqualityComparer<TEntity>.Default.GetHashCode(entity);
                    hash = hash * 397 ^ x.GetHashCode();
                    hash = hash * 397 ^ y.GetHashCode();
                    return hash * 397 ^ z.GetHashCode();
                }
            }
        }
    }
}
