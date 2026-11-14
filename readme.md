# DzbSharp

DZB object collision reading and writing (*Skyward Sword HD* `dzb/*.dzb`, and other Zelda titles that use it).

```csharp
DzbFile dzb = DzbFile.FromBinary(arc["dzb/BRockWall00.dzb"]!.Data);
foreach (var (a, b, c) in dzb.Faces()) Console.WriteLine($"{a} {b} {c}");
byte[] packed = dzb.Write();
```

A DZB is a header of (count, offset) pairs, then vertices (3 floats), triangles (3 vertex indices, property, group),
octree nodes (flags, parent, 8 children), triangle blocks, and a group. The group and anything after it are kept as
bytes, with its name, scale and translation readable; its absolute name offset is moved with it. Bytes found between
sections are kept, so a file rebuilds exactly. The octree is not rebuilt.

All 401 distinct DZB files in the dump rebuild to the same bytes; each has one group, named `Root`.

## Build

```bash
dotnet build DzbSharp.slnx -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
