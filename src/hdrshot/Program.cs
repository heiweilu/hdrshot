using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace HdrShot;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex}");
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 3 && args[0] == "comparepng")
        {
            return PngCompare.Run(args[1], args[2]);
        }

        var inputs = new List<string>();
        string? outDir = null;
        string format = "both";
        int quality = 60;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-o":
                    outDir = args[++i];
                    break;
                case "-f":
                    format = args[++i].ToLowerInvariant();
                    break;
                case "-q":
                    quality = int.Parse(args[++i]);
                    break;
                case "--avifenc":
                    Environment.SetEnvironmentVariable("HDRSHOT_AVIFENC", args[++i]);
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
                default:
                    if (a.Contains('*') || a.Contains('?'))
                    {
                        string dir = Path.GetDirectoryName(Path.GetFullPath(a)) ?? ".";
                        string pattern = Path.GetFileName(a);
                        inputs.AddRange(Directory.GetFiles(dir, pattern));
                    }
                    else
                    {
                        inputs.Add(a);
                    }
                    break;
            }
        }

        if (inputs.Count == 0)
        {
            PrintHelp();
            return 1;
        }
        if (format != "png" && format != "avif" && format != "both")
        {
            Console.Error.WriteLine("error: -f must be png, avif or both");
            return 1;
        }

        string? avifencPath = ResolveAvifenc();
        if (avifencPath == null && format != "png")
        {
            if (format == "avif")
            {
                Console.Error.WriteLine("error: avifenc.exe not found — put it in tools\\ next to hdrshot.exe or pass --avifenc <path>");
                return 1;
            }
            Console.WriteLine("note: avifenc.exe not found, converting to HDR PNG only");
            format = "png";
        }

        int failures = 0;
        var total = Stopwatch.StartNew();
        foreach (string input in inputs)
        {
            try
            {
                ConvertOne(input, outDir, format, quality, avifencPath);
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"error [{input}]: {ex}");
            }
        }

        Console.WriteLine($"done: {inputs.Count - failures}/{inputs.Count} converted in {total.ElapsedMilliseconds} ms");
        return failures == 0 ? 0 : 1;
    }

    private static void ConvertOne(string input, string? outDir, string format, int quality, string? avifencPath)
    {
        var sw = Stopwatch.StartNew();
        string fullPath = Path.GetFullPath(input);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("input not found", fullPath);

        string baseName = Path.GetFileNameWithoutExtension(fullPath);
        string dir = outDir != null ? Path.GetFullPath(outDir) : Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);

        var (width, height, fmtName, rgba) = WicDecoder.DecodeFloatRgba(fullPath);
        Console.WriteLine($"{Path.GetFileName(fullPath)}: {width}x{height} [{fmtName}] decode {sw.ElapsedMilliseconds} ms");

        var converted = HdrConvert.ScRgbToPq2100(rgba, width, height);
        Console.WriteLine($"  MaxCLL {converted.MaxCllNits} nits / MaxFALL {converted.MaxFallNits} nits, convert {sw.ElapsedMilliseconds} ms");

        string pngPath = Path.Combine(dir, baseName + ".png");
        PngWriter.Write16BitPqRgb(pngPath, converted);
        Console.WriteLine($"  -> {pngPath} ({new FileInfo(pngPath).Length / 1048576.0:F1} MiB, {sw.ElapsedMilliseconds} ms)");

        if (format is "avif" or "both")
        {
            string avifPath = Path.Combine(dir, baseName + ".avif");
            RunAvifenc(avifencPath!, pngPath, avifPath, quality);
            Console.WriteLine($"  -> {avifPath} ({new FileInfo(avifPath).Length / 1048576.0:F1} MiB, {sw.ElapsedMilliseconds} ms)");
        }
    }

    private static void RunAvifenc(string avifencPath, string pngPath, string avifPath, int quality)
    {
        int jobs = Math.Clamp(Environment.ProcessorCount, 1, 64);
        var psi = new ProcessStartInfo
        {
            FileName = avifencPath,
            Arguments = $"--cicp 9/16/9 --depth 10 -q {quality} -j {jobs} -- \"{pngPath}\" \"{avifPath}\"",
            UseShellExecute = false,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start avifenc");
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"avifenc exited with code {p.ExitCode}");
    }

    private static string? ResolveAvifenc()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("HDRSHOT_AVIFENC");
        foreach (string? candidate in new string?[]
        {
            explicitPath,
            Path.Combine(AppContext.BaseDirectory, "tools", "avifenc.exe"),
            Path.Combine(AppContext.BaseDirectory, "avifenc.exe"),
            "avifenc",
        })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            try
            {
                var psi = new ProcessStartInfo(candidate, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                p.WaitForExit(10000);
                if (p.ExitCode == 0) return Path.GetFullPath(candidate);
            }
            catch
            {
                // keep probing
            }
        }
        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            hdrshot — convert Windows/NVIDIA HDR screenshots (.jxr, scRGB float) to shareable HDR formats

            usage: hdrshot <files or wildcards...> [options]

              output HDR PNG (16-bit, BT.2020 + PQ, cICP + cLLi) and HDR AVIF (10-bit)
              — keeps the full HDR brightness of the original screenshot.

            options:
              -f png|avif|both   output format (default: both; falls back to png if avifenc missing)
              -o <dir>           output directory (default: same folder as input)
              -q <0-100>         AVIF quality (default 60)
              --avifenc <path>   path to avifenc.exe (otherwise tools\avifenc.exe or PATH)

            examples:
              hdrshot screenshot.jxr
              hdrshot *.jxr -o D:\share -f avif
              hdrshot (or drag .jxr files onto hdrshot.exe)

            source formats: 128bppRGBAFloat (NVIDIA FP32) / 64bppRGBAHalf (Windows FP16) and other scRGB float variants
            """);
    }
}
