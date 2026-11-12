using System.Buffers.Binary;
using System.Text;

namespace Ets2u.Core.Save;

/// <summary>Where one value sits in a binary save, so it can be overwritten in place.</summary>
public sealed record BsiiField(string Structure, string Name, int ValueType, int Offset, int Length, object Value);

/// <summary>
/// Walks a binary save (BSII, as the game writes it by default) from start to end.
/// The format describes itself: "structure" blocks declare a record type's field names and
/// value types, then data blocks hold records of those types. The walk records where chosen
/// fields sit so a fixed-size value can be patched without touching any other byte.
///
/// Value-type encodings follow SII_Decrypt (github.com/TheLazyTomcat/SII_Decrypt); 0x3B-0x3E
/// appear in BSII version 3 (game 1.62) and are IDs and arrays of IDs.
/// </summary>
public static class BsiiReader
{
    const int StructureBlock = 0;

    /// <summary>Fixed-size value types: byte size of one value.</summary>
    static readonly Dictionary<int, int> FixedSize = new()
    {
        [0x03] = 8,  // packed name token (u64)
        [0x05] = 4,  // float
        [0x07] = 8,  // 2 floats
        [0x09] = 12, // 3 floats
        [0x11] = 12, // 3 ints
        [0x17] = 16, // 4 floats
        [0x19] = 32, // placement: 8 floats
        [0x25] = 4,  // s32
        [0x27] = 4,  // u32
        [0x29] = 2,  // s16
        [0x2B] = 2,  // u16
        [0x2F] = 4,  // u32
        [0x31] = 8,  // s64
        [0x33] = 8,  // u64
        [0x35] = 1,  // bool
        [0x37] = 4,  // ordinal string: index into the structure's table
    };

    /// <summary>Array type: element type. Arrays are a u32 count followed by the elements.</summary>
    static readonly Dictionary<int, int> ArrayOf = new()
    {
        [0x02] = 0x01, [0x04] = 0x03, [0x06] = 0x05, [0x08] = 0x07, [0x0A] = 0x09, [0x12] = 0x11,
        [0x18] = 0x17, [0x1A] = 0x19, [0x26] = 0x25, [0x28] = 0x27, [0x2A] = 0x29, [0x2C] = 0x2B,
        [0x32] = 0x31, [0x34] = 0x33, [0x36] = 0x35, [0x3A] = 0x39, [0x3C] = 0x3B, [0x3E] = 0x3D,
    };

    /// <summary>ID types: a length byte, then that many u64 name parts (0xFF: one u64, a nameless id).</summary>
    static readonly HashSet<int> IdTypes = [0x38, 0x39, 0x3B, 0x3D];

    const int StringType = 0x01;

    public static bool IsBinary(ReadOnlySpan<byte> plain) => plain.StartsWith("BSII"u8);

    /// <summary>
    /// Walks the whole file and returns every occurrence of the wanted fields (structure name, field name).
    /// Throws <see cref="NotSupportedException"/> on a value type it doesn't know, so it never guesses.
    /// </summary>
    public static IReadOnlyList<BsiiField> Find(byte[] plain, IReadOnlyCollection<(string Structure, string Field)> wanted)
    {
        if (!IsBinary(plain)) throw new InvalidDataException("Not a binary save.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(4));
        if (version is < 1 or > 3) throw new NotSupportedException($"Binary save version {version} isn't supported yet.");

        var structures = new Dictionary<uint, (string Name, List<(int Type, string Name)> Fields)>();
        var found = new List<BsiiField>();
        var p = 8;

        while (true)
        {
            var block = U32(plain, ref p);
            if (block == StructureBlock)
            {
                if (plain[p++] == 0) break; // end of file marker
                var id = U32(plain, ref p);
                var name = Str(plain, ref p);
                var fields = new List<(int, string)>();
                for (int type; (type = (int)U32(plain, ref p)) != 0;)
                {
                    var fieldName = Str(plain, ref p);
                    if (type == 0x37)
                        for (var n = U32(plain, ref p); n > 0; n--) { p += 4; Str(plain, ref p); }
                    fields.Add((type, fieldName));
                }
                structures[id] = (name, fields);
                continue;
            }

            if (!structures.TryGetValue(block, out var structure))
                throw new InvalidDataException($"Record of undeclared type {block} at byte {p - 4}.");
            SkipId(plain, ref p);
            foreach (var (type, fieldName) in structure.Fields)
            {
                var start = p;
                Skip(plain, ref p, type, $"{structure.Name}.{fieldName}");
                if (wanted.Contains((structure.Name, fieldName)) && FixedSize.ContainsKey(type))
                    found.Add(new BsiiField(structure.Name, fieldName, type, start, p - start, ReadFixed(plain, start, type)));
            }
        }

        if (p != plain.Length) throw new InvalidDataException($"The save ended early: read {p} of {plain.Length} bytes.");
        return found;
    }

    static void Skip(byte[] d, ref int p, int type, string where)
    {
        if (FixedSize.TryGetValue(type, out var size)) { p += size; return; }
        if (type == StringType) { var n = (int)U32(d, ref p); p += n; return; }
        if (IdTypes.Contains(type)) { SkipId(d, ref p); return; }
        if (ArrayOf.TryGetValue(type, out var element))
        {
            for (var n = U32(d, ref p); n > 0; n--) Skip(d, ref p, element, where);
            return;
        }
        throw new NotSupportedException($"Unknown value type 0x{type:x2} in {where}. The game may have changed its save format.");
    }

    static void SkipId(byte[] d, ref int p)
    {
        var length = d[p++];
        p += 8 * (length == 0xFF ? 1 : length);
    }

    static uint U32(byte[] d, ref int p)
    {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
        p += 4;
        return v;
    }

    static string Str(byte[] d, ref int p)
    {
        var n = (int)U32(d, ref p);
        var s = Encoding.UTF8.GetString(d, p, n);
        p += n;
        return s;
    }

    static object ReadFixed(byte[] d, int at, int type) => type switch
    {
        0x25 => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at)),
        0x27 or 0x2F or 0x37 => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)),
        0x31 => BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(at)),
        0x33 => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(at)),
        0x29 => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(at)),
        0x2B => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at)),
        0x35 => d[at] != 0,
        0x05 => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at)),
        _ => d.AsSpan(at, FixedSize[type]).ToArray(),
    };

    /// <summary>Overwrites a whole-number field in place, checking the new value fits its binary type.</summary>
    public static void Patch(byte[] plain, BsiiField field, long value)
    {
        var span = plain.AsSpan(field.Offset, field.Length);
        switch (field.ValueType)
        {
            case 0x27 or 0x2F when value is >= 0 and <= uint.MaxValue:
                BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)value);
                break;
            case 0x25 when value is >= int.MinValue and <= int.MaxValue:
                BinaryPrimitives.WriteInt32LittleEndian(span, (int)value);
                break;
            case 0x31:
                BinaryPrimitives.WriteInt64LittleEndian(span, value);
                break;
            case 0x33 when value >= 0:
                BinaryPrimitives.WriteUInt64LittleEndian(span, (ulong)value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value),
                    $"{value} doesn't fit {field.Structure}.{field.Name} (binary type 0x{field.ValueType:x2}).");
        }
    }
}
