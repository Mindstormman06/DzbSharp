using System.Numerics;

namespace DzbSharp;

public sealed partial class DzbFile
{
    /// <summary>
    /// Rebuilds the octree and blocks from the current vertices and triangles, re-ordering the triangles so each
    /// leaf's triangles are one contiguous block. Triangles go to the octant of their centroid, inside the vertices'
    /// bounding box. The file stores no bounds, so the game is taken to work them out from each block's triangles.
    /// </summary>
    public void RebuildTree(int maxTrianglesPerBlock = 8, int maxDepth = 8)
    {
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        Vector3[] centroids = new Vector3[Triangles.Count];
        for (int i = 0; i < Triangles.Count; i++)
        {
            DzbTriangle t = Triangles[i];
            Vector3 a = Vertices[t.A], b = Vertices[t.B], c = Vertices[t.C];
            centroids[i] = (a + b + c) / 3;
            lo = Vector3.Min(lo, Vector3.Min(a, Vector3.Min(b, c)));
            hi = Vector3.Max(hi, Vector3.Max(a, Vector3.Max(b, c)));
        }
        if (Triangles.Count == 0) { lo = Vector3.Zero; hi = Vector3.One; }

        List<DzbTriangle> ordered = new(Triangles.Count);
        List<ushort> blocks = [];
        List<DzbTreeNode> nodes = [];

        ushort Build(List<int> tris, Vector3 min, Vector3 max, ushort parent, int depth)
        {
            ushort index = (ushort)nodes.Count;
            DzbTreeNode node = new() { Parent = parent };
            nodes.Add(node);

            if (tris.Count <= maxTrianglesPerBlock || depth >= maxDepth)
            {
                node.Kind = 1;
                node.Children[0] = (ushort)blocks.Count;
                blocks.Add((ushort)ordered.Count);
                foreach (int t in tris) ordered.Add(Triangles[t]);
                return index;
            }

            Vector3 mid = (min + max) / 2;
            List<int>[] octants = [.. Enumerable.Range(0, 8).Select(_ => new List<int>())];
            foreach (int t in tris)
            {
                Vector3 c = centroids[t];
                octants[(c.X >= mid.X ? 1 : 0) | (c.Y >= mid.Y ? 2 : 0) | (c.Z >= mid.Z ? 4 : 0)].Add(t);
            }

            for (int k = 0; k < 8; k++)
            {
                if (octants[k].Count == 0) continue;
                Vector3 childMin = new((k & 1) != 0 ? mid.X : min.X, (k & 2) != 0 ? mid.Y : min.Y, (k & 4) != 0 ? mid.Z : min.Z);
                Vector3 childMax = new((k & 1) != 0 ? max.X : mid.X, (k & 2) != 0 ? max.Y : mid.Y, (k & 4) != 0 ? max.Z : mid.Z);
                node.Children[k] = Build(octants[k], childMin, childMax, index, depth + 1);
            }
            return index;
        }

        Build([.. Enumerable.Range(0, Triangles.Count)], lo, hi, 0xFFFF, 0);
        if (nodes.Count > 0xFFFF || blocks.Count > 0xFFFF) throw new InvalidOperationException("Too many tree nodes or blocks for a DZB.");

        Triangles.Clear();
        Triangles.AddRange(ordered);
        Blocks.Clear();
        Blocks.AddRange(blocks);
        TreeNodes.Clear();
        TreeNodes.AddRange(nodes);
        Gaps[1] = [];
        Gaps[2] = [];
        Gaps[3] = new byte[(4 - Blocks.Count * 2 % 4) % 4];
    }
}
