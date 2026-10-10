using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ZompiercerLAN
{
    // Native BinarySaver boundary only. Never use on the LAN inventory ledger/wire.
    internal static class LanBeaverSaveCodec
    {
        internal const int Marker = -4095090;

        internal static object Transform(object root, int customId, int fallbackId, bool saving,
            bool restoreCustom, out int changed)
        {
            changed = 0;
            var source = root as object[];
            if (source == null) return root;
            var clones = new Dictionary<object[], object[]>(ReferenceComparer.Instance);
            var arrays = new List<object[]>();
            clones.Add(source, (object[])source.Clone());
            arrays.Add(source);
            // Iteration handles cyclic and deeply nested graphs without recursion.
            long slots = 0;
            for (int n = 0; n < arrays.Count; n++)
            {
                var current = arrays[n];
                slots += current.Length;
                if (slots > 16000000 || arrays.Count > 1000000)
                    throw new InvalidOperationException("Native save graph exceeds safe clone limits.");
                var copy = clones[current];
                for (int i = 0; i < current.Length; i++)
                {
                    var child = current[i] as object[];
                    if (child == null) continue;
                    object[] childCopy;
                    if (!clones.TryGetValue(child, out childCopy))
                    {
                        childCopy = (object[])child.Clone(); // preserves object[][] and other covariant array types
                        clones.Add(child, childCopy);
                        arrays.Add(child);
                    }
                    copy[i] = childCopy;
                }
            }
            foreach (var current in arrays)
            {
                int idIndex = EntryIdIndex(current);
                if (idIndex < 0) continue;
                int id = (int)current[idIndex];
                var extra = (object[])current[idIndex + 2];
                bool tagged = extra[0] is int && (int)extra[0] == Marker;
                if (saving ? id != customId : id != fallbackId || !tagged) continue;
                if (!saving && !restoreCustom) { changed++; continue; }
                if (saving && extra[0] != null && !tagged)
                    throw new InvalidOperationException("Custom figurine has unexpected native extra data; save aborted.");
                var copy = clones[current];
                // A private extra array prevents a shared extra object from tagging a vanilla item.
                var extraCopy = (object[])clones[extra].Clone();
                extraCopy[0] = saving ? (object)Marker : null;
                copy[idIndex] = saving ? fallbackId : customId;
                copy[idIndex + 2] = extraCopy;
                changed++;
            }
            return clones[source];
        }

        private static int EntryIdIndex(object[] entry)
        {
            int index;
            if (entry.GetType() != typeof(object[])) return -1;
            if (entry.Length == 3) index = 0;
            else if (entry.Length == 4 && IsTransform(entry[0])) index = 1;
            else return -1;
            if (!(entry[index] is int) || !(entry[index + 1] is int))
                return -1;
            var extra = entry[index + 2] as object[];
            if (extra == null || extra.GetType() != typeof(object[]) || extra.Length != 4 ||
                extra[1] != null || (extra[2] != null && !(extra[2] is int)) ||
                (extra[3] != null && !(extra[3] is object[]))) return -1;
            return index;
        }

        private static bool IsTransform(object value)
        {
            var values = value as object[];
            if (values == null || values.Length != 6) return false;
            for (int i = 0; i < values.Length; i++) if (!(values[i] is float)) return false;
            return true;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object[]>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(object[] x, object[] y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object[] value) { return RuntimeHelpers.GetHashCode(value); }
        }
    }
}
