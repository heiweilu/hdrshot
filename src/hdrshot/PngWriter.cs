using System;
using System.IO;
using System.IO.Compression;

namespace HdrShot;

/// <summary>
/// Minimal 16-bit truecolor PNG writer with HDR metadata:
///   cICP = (9, 16, 0, 1): BT.2020 primaries, ST 2084 (PQ) transfer, identity matrix (RGB), full range.
///   cHRM = BT.2020 chromaticities (same as avifdec/libpng; required for correct Chrome HDR display).
///   cLLi = MaxCLL / MaxFALL in 0.0001 nits as 32-bit BE (same as ledoge/jxr_to_png).
/// Zero external dependencies (deflate via System.IO.Compression).
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static void Write16BitPqRgb(string path, ConvertResult img)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        fs.Write(Signature);

        // IHDR
        var ihdr = new byte[13];
        WriteU32(ihdr, 0, img.Width);
        WriteU32(ihdr, 4, img.Height);
        ihdr[8] = 16;  // bit depth
        ihdr[9] = 2;   // color type: truecolor (RGB)
        ihdr[10] = 0;  // compression: deflate
        ihdr[11] = 0;  // filter: adaptive (all rows use filter type 0)
        ihdr[12] = 0;  // interlace: none
        WriteChunk(fs, "IHDR"u8, ihdr);

        // cICP — must appear before IDAT (PNG third edition)
        WriteChunk(fs, "cICP"u8, new byte[] { 9, 16, 0, 1 });

        // cHRM — BT.2020 chromaticities (x100000, BE): D65 white, R, G, B.
        // Same values avifdec/libpng write. Chrome's GPU/HDR path mis-renders
        // PQ PNGs that carry cLLi but no cHRM (yellowish/desaturated); writing
        // cHRM fixes it. Verified by user A/B test (B-cHRM.png variant).
        var chrm = new byte[32];
        uint[] chrmVals = { 31270, 32900, 70800, 29200, 17000, 79700, 13100, 4600 };
        for (int i = 0; i < 8; i++) WriteU32(chrm, i * 4, chrmVals[i]);
        WriteChunk(fs, "cHRM"u8, chrm);

        // cLLi
        var clli = new byte[8];
        WriteU32(clli, 0, (uint)(img.MaxCllNits * 10000));
        WriteU32(clli, 4, (uint)(img.MaxFallNits * 10000));
        WriteChunk(fs, "cLLi"u8, clli);

        // IDAT — raw scanlines: [filter byte 0][R16 BE][G16 BE][B16 BE] per pixel
        long rowBytes = 1 + (long)img.Width * 6;
        var raw = new byte[rowBytes * img.Height];
        long dst = 0;
        for (uint y = 0; y < img.Height; y++)
        {
            raw[dst++] = 0; // filter type: none
            long src = (long)y * img.Width * 3;
            for (uint x = 0; x < img.Width; x++)
            {
                ushort r = img.Pixels[src++];
                ushort g = img.Pixels[src++];
                ushort b = img.Pixels[src++];
                raw[dst++] = (byte)(r >> 8);
                raw[dst++] = (byte)r;
                raw[dst++] = (byte)(g >> 8);
                raw[dst++] = (byte)g;
                raw[dst++] = (byte)(b >> 8);
                raw[dst++] = (byte)b;
            }
        }

        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }
        WriteChunk(fs, "IDAT"u8, compressed.ToArray());

        WriteChunk(fs, "IEND"u8, Array.Empty<byte>());
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        WriteU32(len, (uint)data.Length);
        s.Write(len);

        var body = new byte[4 + data.Length];
        type.CopyTo(body);
        data.CopyTo(body, 4);
        s.Write(body);

        Span<byte> crc = stackalloc byte[4];
        WriteU32(crc, Crc32(body));
        s.Write(crc);
    }

    private static void WriteU32(Span<byte> b, uint v)
    {
        b[0] = (byte)(v >> 24);
        b[1] = (byte)(v >> 16);
        b[2] = (byte)(v >> 8);
        b[3] = (byte)v;
    }

    private static void WriteU32(byte[] b, int offset, uint v)
    {
        b[offset] = (byte)(v >> 24);
        b[offset + 1] = (byte)(v >> 16);
        b[offset + 2] = (byte)(v >> 8);
        b[offset + 3] = (byte)v;
    }

    private static uint[]? _crcTable;

    private static uint Crc32(byte[] data)
    {
        if (_crcTable == null)
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            _crcTable = table;
        }

        uint crc = 0xFFFFFFFF;
        foreach (byte t in data) crc = _crcTable[(crc ^ t) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
