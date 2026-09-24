using System;

namespace HdrShot;

internal sealed class ConvertResult
{
    public required ushort[] Pixels { get; init; }   // RGB, 3 ushorts per pixel, values already shifted into the 16-bit container
    public required uint Width { get; init; }
    public required uint Height { get; init; }
    public required int MaxCllNits { get; init; }
    public required int MaxFallNits { get; init; }
}

/// <summary>
/// scRGB linear float (1.0 == 80 nits) → BT.2020 / ST 2084 (PQ, 10000 nits) quantized RGB.
/// Math verified against ledoge/jxr_to_png (field-proven) and ITU-R BT.2100 constants.
/// </summary>
internal static class HdrConvert
{
    // scRGB(BT.709 primaries, linear) -> BT.2020 primaries, with the 80 nits -> 10000 nits
    // normalization folded in (Y_pq = nits / 10000). Row-vector * matrix convention, exactly as
    // XMVector3Transform in jxr_to_png. Columns each sum to 1/125, preserving D65 white.
    private const double M00 = 2939026994.0 / 585553224375.0;
    private const double M01 = 76515593.0 / 138420033750.0;
    private const double M02 = 12225392.0 / 93230009375.0;
    private const double M10 = 9255011753.0 / 3513319346250.0;
    private const double M11 = 6109575001.0 / 830520202500.0;
    private const double M12 = 1772384008.0 / 2517210253125.0;
    private const double M20 = 173911579.0 / 501902763750.0;
    private const double M21 = 75493061.0 / 830520202500.0;
    private const double M22 = 18035212433.0 / 2517210253125.0;

    // ST 2084 (PQ) constants — ITU-R BT.2100
    private const double PqM1 = 1305.0 / 8192.0;    // 0.1593017578125
    private const double PqM2 = 2523.0 / 32.0;      // 78.84375
    private const double PqC1 = 107.0 / 128.0;      // 0.8359375
    private const double PqC2 = 2413.0 / 128.0;     // 18.8515625
    private const double PqC3 = 2392.0 / 128.0;     // 18.6875

    private const double MaxCllPercentile = 0.9999; // same trade-off as jxr_to_png: avoids Chromium dimming the whole image

    public static ConvertResult ScRgbToPq2100(float[] rgba, uint width, uint height, int dataBits = 10, int containerBits = 16)
    {
        long count = (long)width * height;
        var outPixels = new ushort[count * 3];
        var nitHistogram = new int[10001];   // per-pixel max component, integer nits (0..10000)
        double sumMaxNits = 0;
        int maxNits = 0;

        long maxTarget = (1L << dataBits) - 1;
        int shift = containerBits - dataBits;
        long containerMax = (1L << containerBits) - 1;

        // Thresholds for nearest-in-PQ quantization: code t covers PQ codes (t-0.5, t+0.5].
        // Equivalent to round(pq(linear) * maxTarget), but avoids 2 pow() calls per channel.
        var thresholds = new double[maxTarget + 1];   // thresholds[t] = linear Y whose PQ code is exactly t - 0.5
        for (long t = 0; t <= maxTarget; t++)
        {
            double code = t - 0.5;
            if (code < 0) code = 0;
            thresholds[t] = PqInverse(code / maxTarget);
        }

        for (long p = 0; p < count; p++)
        {
            double r = rgba[p * 4 + 0], g = rgba[p * 4 + 1], b = rgba[p * 4 + 2];

            double cr = r * M00 + g * M10 + b * M20;
            double cg = r * M01 + g * M11 + b * M21;
            double cb = r * M02 + g * M12 + b * M22;

            cr = Math.Clamp(cr, 0.0, 1.0);
            cg = Math.Clamp(cg, 0.0, 1.0);
            cb = Math.Clamp(cb, 0.0, 1.0);

            double maxComp = Math.Max(cr, Math.Max(cg, cb));   // in units of 10000 nits
            int nits = (int)Math.Round(maxComp * 10000.0);
            nitHistogram[nits]++;
            sumMaxNits += nits;
            if (nits > maxNits) maxNits = nits;

            outPixels[p * 3 + 0] = (ushort)Math.Min(Quantize(cr, thresholds, maxTarget) << shift, containerMax);
            outPixels[p * 3 + 1] = (ushort)Math.Min(Quantize(cg, thresholds, maxTarget) << shift, containerMax);
            outPixels[p * 3 + 2] = (ushort)Math.Min(Quantize(cb, thresholds, maxTarget) << shift, containerMax);
        }

        // MaxCLL: brightest nit level that covers at least 0.01% of pixels
        long countTarget = (long)Math.Round((1.0 - MaxCllPercentile) * count);
        long acc = 0;
        int maxCll = maxNits;
        for (int idx = maxNits; idx >= 0; idx--)
        {
            acc += nitHistogram[idx];
            if (acc >= countTarget) { maxCll = idx; break; }
        }
        int maxFall = (int)Math.Round(sumMaxNits / count);

        return new ConvertResult { Pixels = outPixels, Width = width, Height = height, MaxCllNits = maxCll, MaxFallNits = maxFall };
    }

    private static long Quantize(double linear, double[] thresholds, long maxTarget)
    {
        // binary search: largest t such that linear >= thresholds[t]
        long lo = 0, hi = maxTarget;
        while (lo < hi)
        {
            long mid = (lo + hi + 1) / 2;
            if (linear >= thresholds[mid]) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>PQ EOTF inverse domain conversion: linear Y (0..1 == 0..10000 nits) → PQ code (0..1).</summary>
    private static double PqEncode(double y)
    {
        double ym = Math.Pow(y, PqM1);
        return Math.Pow((PqC1 + PqC2 * ym) / (1.0 + PqC3 * ym), PqM2);
    }

    /// <summary>PQ code (0..1) → linear Y (0..1 == 0..10000 nits).</summary>
    private static double PqInverse(double e)
    {
        double ep = Math.Pow(e, 1.0 / PqM2);
        double num = Math.Max(ep - PqC1, 0.0);
        double den = PqC2 - PqC3 * ep;
        return den > 0 ? Math.Pow(num / den, 1.0 / PqM1) : 0.0;
    }

    /// <summary>Exposes PQ encoding + threshold table for tests (nits in → 10-bit PQ code out).</summary>
    public static long NitsToPqCode(double nits, int dataBits = 10)
    {
        double y = nits / 10000.0;
        double maxTarget = (1 << dataBits) - 1;
        double code = PqEncode(Math.Clamp(y, 0.0, 1.0)) * maxTarget;
        return (long)Math.Round(code);
    }
}
