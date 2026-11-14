using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace DzbSharp;

public struct DzbTriangle
{
    public ushort A;
    public ushort B;
    public ushort C;
    public ushort Property;
    public ushort Group;
}

/// <summary>An octree node. A branch's children are node indices by octant (x in bit 0, y bit 1, z bit 2), 0xFFFF
/// for empty; a leaf's first child is a block index.</summary>
public sealed class DzbTreeNode
{
    public byte Flags { get; set; } = 1;
    public byte Kind { get; set; }
    public ushort Parent { get; set; } = 0xFFFF;
    public ushort[] Children { get; } = [0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF];

    public bool IsLeaf => Kind != 0;
}

/// <summary>A DZB collision file: vertices, triangles, an octree of triangle blocks, and a group.</summary>
public sealed partial class DzbFile
{
    private const int HeaderSize = 0x34;

    public List<Vector3> Vertices { get; } = [];
    public List<DzbTriangle> Triangles { get; } = [];
    public List<DzbTreeNode> TreeNodes { get; } = [];
    public List<ushort> Blocks { get; } = [];

    /// <summary>The group and everything after it (name, padding, properties), as bytes. Its name offset is absolute.</summary>
    public byte[] GroupData { get; set; } = [];
    public int GroupCount { get; set; } = 1;
    public uint PropertyCount { get; set; }
    public uint PropertyOffset { get; set; }
    public uint Reserved { get; set; }

    /// <summary>Bytes found after each section, before the next one, in file order (vertices, triangles, tree, blocks).</summary>
    public byte[][] Gaps { get; } = [[], [], [], []];

    private int _groupStart;

    public string GroupName
    {
        get
        {
            if (GroupData.Length < 4) return "";
            int at = (int)BinaryPrimitives.ReadUInt32BigEndian(GroupData) - _groupStart;
            if (at < 0 || at >= GroupData.Length) return "";
            int end = Array.IndexOf(GroupData, (byte)0, at);
            return Encoding.Latin1.GetString(GroupData, at, (end < 0 ? GroupData.Length : end) - at);
        }
    }

    public Vector3 GroupScale => ReadVec3(4);
    public Vector3 GroupTranslation => ReadVec3(0x18);

    public IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> Faces()
        => Triangles.Select(t => (Vertices[t.A], Vertices[t.B], Vertices[t.C]));

    public static DzbFile FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    public static DzbFile FromBinary(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) throw new InvalidDataException("DZB is too short.");
        var vertices = Pair(data, 0);
        var triangles = Pair(data, 1);
        var blocks = Pair(data, 2);
        var tree = Pair(data, 3);
        var group = Pair(data, 4);

        DzbFile dzb = new()
        {
            GroupCount = group.Count, PropertyCount = U32(data, 0x28), PropertyOffset = U32(data, 0x2C), Reserved = U32(data, 0x30),
            _groupStart = group.Offset,
        };
        if (vertices.Offset != HeaderSize || group.Offset > data.Length || group.Offset < blocks.Offset)
            throw new InvalidDataException("DZB section offsets are not in the expected order.");

        for (int i = 0; i < vertices.Count; i++) dzb.Vertices.Add(Vec3(data, vertices.Offset + i * 12));
        for (int i = 0; i < triangles.Count; i++)
        {
            int at = triangles.Offset + i * 10;
            dzb.Triangles.Add(new DzbTriangle { A = U16(data, at), B = U16(data, at + 2), C = U16(data, at + 4), Property = U16(data, at + 6), Group = U16(data, at + 8) });
        }
        for (int i = 0; i < tree.Count; i++)
        {
            int at = tree.Offset + i * 20;
            DzbTreeNode node = new() { Flags = data[at], Kind = data[at + 1], Parent = U16(data, at + 2) };
            for (int c = 0; c < 8; c++) node.Children[c] = U16(data, at + 4 + c * 2);
            dzb.TreeNodes.Add(node);
        }
        for (int i = 0; i < blocks.Count; i++) dzb.Blocks.Add(U16(data, blocks.Offset + i * 2));

        dzb.Gaps[0] = data[(vertices.Offset + vertices.Count * 12)..triangles.Offset].ToArray();
        dzb.Gaps[1] = data[(triangles.Offset + triangles.Count * 10)..tree.Offset].ToArray();
        dzb.Gaps[2] = data[(tree.Offset + tree.Count * 20)..blocks.Offset].ToArray();
        dzb.Gaps[3] = data[(blocks.Offset + blocks.Count * 2)..group.Offset].ToArray();
        dzb.GroupData = data[group.Offset..].ToArray();
        return dzb;
    }

    public void WriteTo(string path) => File.WriteAllBytes(path, Write());

    public byte[] Write()
    {
        using MemoryStream o = new();
        o.Write(new byte[HeaderSize]);

        int vertices = (int)o.Length;
        foreach (Vector3 v in Vertices) { Write32(o, v.X); Write32(o, v.Y); Write32(o, v.Z); }
        o.Write(Gaps[0]);

        int triangles = (int)o.Length;
        foreach (DzbTriangle t in Triangles) { Write16(o, t.A); Write16(o, t.B); Write16(o, t.C); Write16(o, t.Property); Write16(o, t.Group); }
        o.Write(Gaps[1]);

        int tree = (int)o.Length;
        foreach (DzbTreeNode n in TreeNodes)
        {
            o.WriteByte(n.Flags);
            o.WriteByte(n.Kind);
            Write16(o, n.Parent);
            foreach (ushort c in n.Children) Write16(o, c);
        }
        o.Write(Gaps[2]);

        int blocks = (int)o.Length;
        foreach (ushort b in Blocks) Write16(o, b);
        o.Write(Gaps[3]);

        int group = (int)o.Length;
        byte[] groupData = (byte[])GroupData.Clone();
        if (groupData.Length >= 4)
        {
            uint name = BinaryPrimitives.ReadUInt32BigEndian(groupData);
            BinaryPrimitives.WriteUInt32BigEndian(groupData, (uint)(name - _groupStart + group));
        }
        o.Write(groupData);

        byte[] file = o.ToArray();
        SetPair(file, 0, Vertices.Count, vertices);
        SetPair(file, 1, Triangles.Count, triangles);
        SetPair(file, 2, Blocks.Count, blocks);
        SetPair(file, 3, TreeNodes.Count, tree);
        SetPair(file, 4, GroupCount, group);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(0x28), PropertyCount);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(0x2C), PropertyOffset);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(0x30), Reserved);
        return file;
    }

    private static (int Count, int Offset) Pair(ReadOnlySpan<byte> data, int i) => ((int)U32(data, i * 8), (int)U32(data, i * 8 + 4));

    private static void SetPair(byte[] file, int i, int count, int offset)
    {
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(i * 8), (uint)count);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(i * 8 + 4), (uint)offset);
    }

    private Vector3 ReadVec3(int at) => GroupData.Length < at + 12 ? default : new(
        BinaryPrimitives.ReadSingleBigEndian(GroupData.AsSpan(at)), BinaryPrimitives.ReadSingleBigEndian(GroupData.AsSpan(at + 4)),
        BinaryPrimitives.ReadSingleBigEndian(GroupData.AsSpan(at + 8)));

    private static uint U32(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadUInt32BigEndian(d[at..]);
    private static ushort U16(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadUInt16BigEndian(d[at..]);
    private static Vector3 Vec3(ReadOnlySpan<byte> d, int at) => new(
        BinaryPrimitives.ReadSingleBigEndian(d[at..]), BinaryPrimitives.ReadSingleBigEndian(d[(at + 4)..]), BinaryPrimitives.ReadSingleBigEndian(d[(at + 8)..]));

    private static void Write16(Stream o, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); o.Write(b); }
    private static void Write32(Stream o, float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, v); o.Write(b); }
}
