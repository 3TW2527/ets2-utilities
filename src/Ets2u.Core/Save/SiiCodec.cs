using System.IO.Compression;
using System.Security.Cryptography;

namespace Ets2u.Core.Save;

public enum SiiFormat { Text, Binary, Unknown }

/// <summary>
/// Reads the ScsC container the game wraps saves in: a 56-byte header
/// (signature, HMAC, IV, uncompressed size), then AES-256-CBC over zlib data.
/// Layout and key as documented by SII_Decrypt (github.com/TheLazyTomcat/SII_Decrypt).
/// </summary>
public static class SiiCodec
{
    static readonly byte[] Key =
    [
        0x2a, 0x5f, 0xcb, 0x17, 0x91, 0xd2, 0x2f, 0xb6, 0x02, 0x45, 0xb3, 0xd8, 0x36, 0x9e, 0xd0, 0xb2,
        0xc2, 0x73, 0x71, 0x56, 0x3f, 0xbf, 0x1f, 0x3c, 0x9e, 0xdf, 0x6b, 0x11, 0x82, 0x5a, 0x5d, 0x0a,
    ];

    const int HeaderSize = 56;

    public static bool IsEncrypted(ReadOnlySpan<byte> data) => data.StartsWith("ScsC"u8);

    public static SiiFormat Detect(ReadOnlySpan<byte> plain)
    {
        if (plain.StartsWith("BSII"u8)) return SiiFormat.Binary;
        if (plain.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) plain = plain[3..];
        return plain.StartsWith("SiiNunit"u8) ? SiiFormat.Text : SiiFormat.Unknown;
    }

    /// <summary>Unwraps ScsC if present; returns plain bytes unchanged otherwise.</summary>
    public static byte[] Decode(byte[] data)
    {
        if (!IsEncrypted(data)) return data;
        if (data.Length < HeaderSize) throw new InvalidDataException("ScsC file is truncated.");
        var iv = data.AsSpan(36, 16).ToArray();
        var size = BitConverter.ToInt32(data, 52);

        using var aes = Aes.Create();
        aes.Key = Key;
        var compressed = aes.DecryptCbc(data.AsSpan(HeaderSize), iv, PaddingMode.PKCS7);

        using var zlib = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream(size);
        zlib.CopyTo(output);
        if (output.Length != size)
            throw new InvalidDataException($"Decompressed {output.Length} bytes, header says {size}.");
        return output.ToArray();
    }

    /// <summary>Wraps plain bytes in ScsC. The game also reads plain text, so this is only needed for tests.</summary>
    public static byte[] Encode(byte[] plain)
    {
        using var compressedStream = new MemoryStream();
        using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(plain);

        using var aes = Aes.Create();
        aes.Key = Key;
        var iv = RandomNumberGenerator.GetBytes(16);
        var cipher = aes.EncryptCbc(compressedStream.ToArray(), iv, PaddingMode.PKCS7);
        byte[] signed = [.. iv, .. BitConverter.GetBytes(plain.Length), .. cipher];
        var hmac = HMACSHA256.HashData(Key, signed);

        return [.. "ScsC"u8, .. hmac, .. iv, .. BitConverter.GetBytes(plain.Length), .. cipher];
    }
}
