using System.Buffers.Binary;
using System.Numerics;

namespace Ets2u.Core.Extract;

/// <summary>
/// CityHash64 v1.0.3 (Google, MIT licence), the hash HashFS v2 archives key their entries by.
/// Written from the published algorithm.
/// </summary>
public static class CityHash
{
    const ulong K0 = 0xc3a5c85c97cb3127, K1 = 0xb492b66fbe98f273, K2 = 0x9ae16a3b2f90404f, K3 = 0xc949d7c7509e6557;

    static ulong F64(ReadOnlySpan<byte> s, int i) => BinaryPrimitives.ReadUInt64LittleEndian(s[i..]);
    static ulong F32(ReadOnlySpan<byte> s, int i) => BinaryPrimitives.ReadUInt32LittleEndian(s[i..]);
    static ulong Rot(ulong v, int n) => n == 0 ? v : BitOperations.RotateRight(v, n);
    static ulong ShiftMix(ulong v) => v ^ (v >> 47);

    static ulong Len16(ulong u, ulong v)
    {
        const ulong mul = 0x9ddfea08eb382d69;
        var a = (u ^ v) * mul;
        a ^= a >> 47;
        var b = (v ^ a) * mul;
        b ^= b >> 47;
        return b * mul;
    }

    static ulong Len0To16(ReadOnlySpan<byte> s)
    {
        var len = (ulong)s.Length;
        if (len > 8)
        {
            var a = F64(s, 0);
            var b = F64(s, s.Length - 8);
            return Len16(a, BitOperations.RotateRight(b + len, (int)len)) ^ b;
        }
        if (len >= 4) return Len16(len + (F32(s, 0) << 3), F32(s, s.Length - 4));
        if (len > 0)
        {
            uint a = s[0], b = s[s.Length >> 1], c = s[^1];
            var y = a + (b << 8);
            var z = (uint)len + (c << 2);
            return ShiftMix(y * K2 ^ z * K3) * K2;
        }
        return K2;
    }

    static ulong Len17To32(ReadOnlySpan<byte> s)
    {
        var len = (ulong)s.Length;
        var a = F64(s, 0) * K1;
        var b = F64(s, 8);
        var c = F64(s, s.Length - 8) * K2;
        var d = F64(s, s.Length - 16) * K0;
        return Len16(Rot(a - b, 43) + Rot(c, 30) + d, a + Rot(b ^ K3, 20) - c + len);
    }

    static (ulong, ulong) Weak32(ulong w, ulong x, ulong y, ulong z, ulong a, ulong b)
    {
        a += w;
        b = Rot(b + a + z, 21);
        var c = a;
        a += x;
        a += y;
        b += Rot(a, 44);
        return (a + z, b + c);
    }

    static (ulong, ulong) Weak32(ReadOnlySpan<byte> s, int i, ulong a, ulong b) =>
        Weak32(F64(s, i), F64(s, i + 8), F64(s, i + 16), F64(s, i + 24), a, b);

    static ulong Len33To64(ReadOnlySpan<byte> s)
    {
        var len = (ulong)s.Length;
        var n = s.Length;
        var z = F64(s, 24);
        var a = F64(s, 0) + (len + F64(s, n - 16)) * K0;
        var b = Rot(a + z, 52);
        var c = Rot(a, 37);
        a += F64(s, 8);
        c += Rot(a, 7);
        a += F64(s, 16);
        var vf = a + z;
        var vs = b + Rot(a, 31) + c;
        a = F64(s, 16) + F64(s, n - 32);
        z = F64(s, n - 8);
        b = Rot(a + z, 52);
        c = Rot(a, 37);
        a += F64(s, n - 24);
        c += Rot(a, 7);
        a += F64(s, n - 16);
        var wf = a + z;
        var ws = b + Rot(a, 31) + c;
        var r = ShiftMix((vf + ws) * K2 + (wf + vs) * K0);
        return ShiftMix(r * K0 + vs) * K2;
    }

    public static ulong Hash64(ReadOnlySpan<byte> s)
    {
        var n = s.Length;
        if (n <= 32) return n <= 16 ? Len0To16(s) : Len17To32(s);
        if (n <= 64) return Len33To64(s);

        var len = (ulong)n;
        var x = F64(s, n - 40);
        var y = F64(s, n - 16) + F64(s, n - 56);
        var z = Len16(F64(s, n - 48) + len, F64(s, n - 24));
        var v = Weak32(s, n - 64, len, z);
        var w = Weak32(s, n - 32, y + K1, x);
        x = x * K1 + F64(s, 0);

        var remaining = (n - 1) & ~63;
        var i = 0;
        do
        {
            x = Rot(x + y + v.Item1 + F64(s, i + 8), 37) * K1;
            y = Rot(y + v.Item2 + F64(s, i + 48), 42) * K1;
            x ^= w.Item2;
            y += v.Item1 + F64(s, i + 40);
            z = Rot(z + w.Item1, 33) * K1;
            v = Weak32(s, i, v.Item2 * K1, x + w.Item1);
            w = Weak32(s, i + 32, z + w.Item2, y + F64(s, i + 16));
            (z, x) = (x, z);
            i += 64;
            remaining -= 64;
        } while (remaining != 0);

        return Len16(Len16(v.Item1, w.Item1) + ShiftMix(y) * K1 + z, Len16(v.Item2, w.Item2) + x);
    }

    public static ulong Hash64(string s) => Hash64(System.Text.Encoding.UTF8.GetBytes(s));
}
