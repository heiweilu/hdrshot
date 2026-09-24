using System;
using System.Diagnostics;
using System.IO;

namespace HdrShot;

/// <summary>
/// UltraHDR JPEG output via libultrahdr's ultrahdr_app (encode scenario 0):
/// we provide the HDR intent as raw P010 (10-bit PQ BT.2020, narrow range),
/// the library computes the SDR base and the gain map internally.
/// </summary>
internal static class UltraHdrEncoder
{
    /// <summary>
    /// UltraHDR JPEG via libultrahdr ultrahdr_app (encode scenario 1): we provide both intents —
    /// HDR as raw P010 (10-bit PQ BT.2020, narrow) and SDR as raw YUV420 planar I420
    /// (8-bit BT.709 limited, sRGB-encoded) — so the SDR base rendering is fully controlled by
    /// us, and the gain map is computed by the library from the two intents.
    /// </summary>
    public static void EncodeFromPq10(ushort[] pqPixels, byte[] sdrYuv420, uint width, uint height,
        string outputPath, string ultrahdrAppPath, int sdrQuality = 95, int gainMapQuality = 90, int gainMapDownsample = 2)
    {
        if ((width & 1) != 0 || (height & 1) != 0)
            throw new NotSupportedException("P010/I420 require even dimensions");

        string p010Path = Path.Combine(Path.GetDirectoryName(outputPath)!, $".hdrshot_{Guid.NewGuid():N}.p010");
        string yuvPath = Path.Combine(Path.GetDirectoryName(outputPath)!, $".hdrshot_{Guid.NewGuid():N}.i420");
        try
        {
            WriteP010(p010Path, pqPixels, width, height);
            File.WriteAllBytes(yuvPath, sdrYuv420);
            if (Environment.GetEnvironmentVariable("HDRSHOT_KEEP_YUV") == "1")
            {
                File.Copy(yuvPath, Path.Combine(Path.GetDirectoryName(outputPath)!, "debug_sdr.i420"), true);
            }

            var psi = new ProcessStartInfo
            {
                FileName = ultrahdrAppPath,
                Arguments = $"-m 0 -p \"{p010Path}\" -y \"{yuvPath}\" -w {width} -h {height} " +
                            $"-a 0 -b 1 -t 2 -C 2 -c 0 -R 0 " +
                            $"-q {sdrQuality} -Q {gainMapQuality} -s {gainMapDownsample} -z \"{outputPath}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ultrahdr_app");
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(outputPath))
                throw new InvalidOperationException($"ultrahdr_app failed (exit {p.ExitCode}): {err.Trim()}");
        }
        finally
        {
            try { if (File.Exists(p010Path)) File.Delete(p010Path); } catch { /* best effort */ }
            try { if (File.Exists(yuvPath)) File.Delete(yuvPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// RGB PQ 10-bit (full scale) → P010 (YUV 4:2:0, BT.2020nc, narrow range, 10-bit MSB-aligned in 16-bit LE words).
    /// </summary>
    private static void WriteP010(string path, ushort[] pqPixels, uint width, uint height)
    {
        long ySamples = (long)width * height;
        long uvSamples = (long)(width / 2) * (height / 2) * 2;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.SetLength((ySamples + uvSamples) * 2);

        // Y plane
        var yPlane = new byte[ySamples * 2];
        long count = ySamples;
        for (long i = 0; i < count; i++)
        {
            double r = pqPixels[i * 3 + 0] / 1023.0;
            double g = pqPixels[i * 3 + 1] / 1023.0;
            double b = pqPixels[i * 3 + 2] / 1023.0;

            double y = (0.2627 * r + 0.6780 * g + 0.0593 * b) * 876.0 + 64.0;
            ushort y10 = Clamp10(Math.Round(y));
            ushort y16 = (ushort)(y10 << 6); // 10-bit MSB-aligned in 16-bit container
            yPlane[i * 2] = (byte)y16;
            yPlane[i * 2 + 1] = (byte)(y16 >> 8);
        }
        fs.Write(yPlane);

        // Interleaved UV plane, 2x2 subsampled (linear transform ⇒ averaging RGB before conversion is exact)
        var uvPlane = new byte[uvSamples * 2];
        long o = 0;
        for (uint by = 0; by < height; by += 2)
        {
            for (uint bx = 0; bx < width; bx += 2)
            {
                long i00 = (by * width + bx) * 3;
                long i10 = i00 + 3;
                long i01 = i00 + width * 3;
                long i11 = i01 + 3;

                double r = (pqPixels[i00] + pqPixels[i10] + pqPixels[i01] + pqPixels[i11]) / (4.0 * 1023.0);
                double g = (pqPixels[i00 + 1] + pqPixels[i10 + 1] + pqPixels[i01 + 1] + pqPixels[i11 + 1]) / (4.0 * 1023.0);
                double b = (pqPixels[i00 + 2] + pqPixels[i10 + 2] + pqPixels[i01 + 2] + pqPixels[i11 + 2]) / (4.0 * 1023.0);

                double cb = (-0.13963 * r - 0.36037 * g + 0.5 * b) * 896.0 + 512.0;
                double cr = (0.5 * r - 0.45979 * g - 0.04021 * b) * 896.0 + 512.0;

                ushort cb16 = (ushort)(Clamp10(Math.Round(cb)) << 6);
                ushort cr16 = (ushort)(Clamp10(Math.Round(cr)) << 6);

                uvPlane[o++] = (byte)cb16; uvPlane[o++] = (byte)(cb16 >> 8);
                uvPlane[o++] = (byte)cr16; uvPlane[o++] = (byte)(cr16 >> 8);
            }
        }
        fs.Write(uvPlane);
    }

    private static ushort Clamp10(double v)
    {
        if (v < 0) return 0;
        if (v > 1023) return 1023;
        return (ushort)v;
    }
}
