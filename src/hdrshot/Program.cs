using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace HdrShot;

internal static class Program
{
    /// <summary>Default UltraHDR JPEG highlight peak (nits); soft knee sits at peak/4. 0 disables compression.</summary>
    internal const double DefaultJpgPeakNits = 300.0;
    internal const double DefaultJpgScale = 0.45;
    internal const double MinJpgPeakNits = 203.0;

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

    /// <summary>
    /// True when we own the console alone — i.e. launched by double-click / drag-drop from
    /// Explorer. In that case the console window would vanish on exit, so we pause.
    /// </summary>
    private static bool ConsoleOwnedAlone()
    {
        try
        {
            var procs = new uint[2];
            uint n = GetConsoleProcessList(procs, 2);
            return n <= 1;
        }
        catch { return false; }
    }

    [STAThread] // WinForms/OLE 拖放要求 STA；MTA 下 RegisterDragDrop 会挂起 UI 线程
    private static int Main(string[] args)
    {
        try
        {
            int exit = Run(args);
            if (args.Length > 0 && ConsoleOwnedAlone())
            {
                Console.WriteLine();
                Console.Write("按回车键退出... (press Enter to exit)");
                Console.ReadLine();
            }
            return exit;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex}");
            if (args.Length > 0 && ConsoleOwnedAlone())
            {
                Console.Write("按回车键退出... (press Enter to exit)");
                Console.ReadLine();
            }
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            // double-clicked (no arguments) → open the drag-drop GUI
            return Gui.Run();
        }
        if (args.Length == 3 && args[0] == "comparepng")
        {
            return PngCompare.Run(args[1], args[2]);
        }
        if (args.Length == 0 || (args.Length == 1 && args[0] == "--gui"))
        {
            return Gui.Run();
        }
        if (args.Length >= 2 && args[0] == "--watch")
        {
            return Watch(args[1], args);
        }
        if (args.Length == 1 && args[0] == "--install-menus")
        {
            return InstallMenus();
        }
        if (args.Length == 1 && args[0] == "--uninstall-menus")
        {
            return UninstallMenus();
        }

        var inputs = new List<string>();
        string? outDir = null;
        string format = "jpeg";
        int quality = 60;
        double sdrWhite = 0;
        double jpgPeak = DefaultJpgPeakNits;
        double jpgScale = DefaultJpgScale;
        bool jpgFlatBase = false;

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
                case "--sdr-white":
                    sdrWhite = double.Parse(args[++i]);
                    break;
                case "--jpg-peak":
                    jpgPeak = double.Parse(args[++i]);
                    break;
                case "--jpg-scale":
                    jpgScale = double.Parse(args[++i]);
                    break;
                case "--jpg-flat":
                    jpgFlatBase = true;
                    break;
                case "--avifenc":
                    Environment.SetEnvironmentVariable("HDRSHOT_AVIFENC", args[++i]);
                    break;
                case "--ultrahdr":
                    Environment.SetEnvironmentVariable("HDRSHOT_ULTRAHDR", args[++i]);
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
        if (format != "jpeg" && format != "png" && format != "avif" && format != "all" && format != "both")
        {
            Console.Error.WriteLine("error: -f must be jpeg, png, avif or all");
            return 1;
        }
        if (format == "both") format = "all";

        bool wantPng = format is "png" or "all";
        bool wantAvif = format is "avif" or "all";
        bool wantJpeg = format is "jpeg" or "all";

        string? avifencPath = ResolveTool("avifenc.exe", "--version", "HDRSHOT_AVIFENC");
        if (wantAvif && avifencPath == null)
        {
            if (format == "avif")
            {
                Console.Error.WriteLine("error: avifenc.exe not found — put it in tools\\ next to hdrshot.exe or pass --avifenc <path>");
                return 1;
            }
            Console.WriteLine("note: avifenc.exe not found, skipping HDR AVIF output");
            wantAvif = false;
        }

        string? ultrahdrPath = ResolveTool("ultrahdr_app.exe", "--bogus-flag", "HDRSHOT_ULTRAHDR");
        if (wantJpeg && ultrahdrPath == null)
        {
            if (format == "jpeg")
            {
                Console.Error.WriteLine("error: ultrahdr_app.exe not found — put it in tools\\ next to hdrshot.exe or pass --ultrahdr <path>");
                return 1;
            }
            Console.WriteLine("note: ultrahdr_app.exe not found, skipping UltraHDR JPEG output");
            wantJpeg = false;
        }

        if (!wantPng && !wantAvif && !wantJpeg)
        {
            Console.Error.WriteLine("error: no output format available");
            return 1;
        }

        int failures = 0;
        var total = Stopwatch.StartNew();
        foreach (string input in inputs)
        {
            try
            {
                ConvertOne(input, outDir, wantPng, wantAvif, wantJpeg, quality, sdrWhite, jpgPeak, jpgScale, jpgFlatBase, avifencPath, ultrahdrPath);
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"error [{input}]: {ex.Message}");
            }
        }

        Console.WriteLine($"done: {inputs.Count - failures}/{inputs.Count} converted in {total.ElapsedMilliseconds} ms");
        return failures == 0 ? 0 : 1;
    }

    internal static void ConvertOne(string input, string? outDir, bool wantPng, bool wantAvif, bool wantJpeg,
        int quality, double sdrWhite, double jpgPeak, double jpgScale, bool jpgFlatBase, string? avifencPath, string? ultrahdrPath)
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

        if (wantJpeg)
        {
            var jpgPixels = converted.Pixels;
            if (jpgScale != 1.0)
            {
                jpgPixels = HdrConvert.ScalePq(jpgPixels, jpgScale);
                Console.WriteLine($"  UltraHDR JPG: overall luminance scale x{jpgScale:F2} (midtones included)");
            }
            if (jpgPeak > 0)
            {
                if (jpgPeak < MinJpgPeakNits)
                {
                    Console.WriteLine($"  note: jpg-peak {jpgPeak:F0} nits below the PQ floor, clamped to {MinJpgPeakNits:F0}");
                    jpgPeak = MinJpgPeakNits;
                }
                jpgPixels = HdrConvert.CompressPqHighlightsLuma(jpgPixels, jpgPeak / 4.0, jpgPeak);
                Console.WriteLine($"  UltraHDR JPG: luminance soft knee above {jpgPeak / 4.0:F0} nits, rolling off toward {jpgPeak:F0} nits");
            }

            byte[] sdrYuv;
            double usedWhite;
            if (jpgFlatBase)
            {
                // 基底直接从处理后的 HDR 意图生成：增益图 ≈1.0，暗部/中间调不依赖查看器
                // 对 <1 增益的应用力度，任何查看器下渲染结果一致（所见即所得）。
                usedWhite = sdrWhite > 0 ? sdrWhite : Math.Clamp(converted.MaxFallNits * 2.0, 203.0, 1000.0);
                sdrYuv = HdrConvert.Pq10ToSdrYuv420(jpgPixels, width, height, usedWhite);
                Console.WriteLine($"  UltraHDR SDR base: from processed intent, white = {usedWhite:F0} nits (gain map ~1.0, viewer-independent)");
            }
            else
            {
                // SDR 基底保持自动白点（不随峰值缩小）：Chrome 会响应 <1 的减光增益，
                // 压低峰值只会压暗高光，不会抬亮中间调
                sdrYuv = HdrConvert.ScRgbToSdrYuv420(rgba, width, height, converted.MaxFallNits, out usedWhite, sdrWhite);
                Console.WriteLine($"  UltraHDR SDR base: white = {usedWhite:F0} nits (mapped from {converted.MaxFallNits} nits MaxFALL)");
            }

            string jpegPath = Path.Combine(dir, baseName + ".jpg");
            UltraHdrEncoder.EncodeFromPq10(jpgPixels, sdrYuv, width, height, jpegPath, ultrahdrPath!, targetPeakNits: jpgPeak);
            Console.WriteLine($"  -> {jpegPath} ({new FileInfo(jpegPath).Length / 1048576.0:F1} MiB, {sw.ElapsedMilliseconds} ms)");
        }

        if (wantPng || wantAvif)
        {
            string pngPath = Path.Combine(dir, baseName + ".png");
            PngWriter.Write16BitPqRgb(pngPath, converted);
            Console.WriteLine($"  -> {pngPath} ({new FileInfo(pngPath).Length / 1048576.0:F1} MiB, {sw.ElapsedMilliseconds} ms)");

            if (wantAvif)
            {
                string avifPath = Path.Combine(dir, baseName + ".avif");
                RunAvifenc(avifencPath!, pngPath, avifPath, quality);
                Console.WriteLine($"  -> {avifPath} ({new FileInfo(avifPath).Length / 1048576.0:F1} MiB, {sw.ElapsedMilliseconds} ms)");
            }
        }
    }

    internal static void RunAvifenc(string avifencPath, string pngPath, string avifPath, int quality)
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

    private static string? ResolveTool(string exeName, string probeArgs, string envVarName)
    {
        string? explicitPath = Environment.GetEnvironmentVariable(envVarName);
        foreach (string? candidate in new string?[]
        {
            explicitPath,
            Path.Combine(AppContext.BaseDirectory, "tools", exeName),
            Path.Combine(AppContext.BaseDirectory, exeName),
            exeName,
        })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            try
            {
                var psi = new ProcessStartInfo(candidate, probeArgs)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                p.WaitForExit(20000);
                return Path.GetFullPath(candidate);
            }
            catch
            {
                // keep probing
            }
        }
        return null;
    }

    internal static string RequireTool(string exeName, string envVarName, string probe = "--version")
        => ResolveTool(exeName, probe, envVarName)
           ?? throw new InvalidOperationException($"{exeName} not found — put it in tools\\ next to hdrshot.exe");

    // ---- watch folder mode ----

    private static int Watch(string dir, string[] args)
    {
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"error: watch directory not found: {dir}");
            return 1;
        }

        string? outDir = null;
        string format = "jpeg";
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o": outDir = args[++i]; break;
                case "-f": format = args[++i].ToLowerInvariant(); break;
            }
        }
        bool wantPng = format is "png" or "all";
        bool wantAvif = format is "avif" or "all";
        bool wantJpeg = format is "jpeg" or "all";

        string? avifencPath = wantAvif ? ResolveTool("avifenc.exe", "--version", "HDRSHOT_AVIFENC") : null;
        string? ultrahdrPath = wantJpeg ? ResolveTool("ultrahdr_app.exe", "--watch-probe", "HDRSHOT_ULTRAHDR") : null;
        if ((wantAvif && avifencPath == null) || (wantJpeg && ultrahdrPath == null))
        {
            Console.Error.WriteLine("error: required encoder not found in tools\\");
            return 1;
        }

        Console.WriteLine($"watching {Path.GetFullPath(dir)} for new .jxr files — Ctrl+C to stop");
        using var watcher = new FileSystemWatcher(Path.GetFullPath(dir), "*.jxr")
        {
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, e) =>
        {
            try
            {
                // screenshots are written asynchronously — wait for the writer to finish
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        using var fs = File.Open(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.None);
                        break;
                    }
                    catch (IOException)
                    {
                        System.Threading.Thread.Sleep(700);
                    }
                }
                ConvertOne(e.FullPath, outDir, wantPng, wantAvif, wantJpeg, 60, 0, DefaultJpgPeakNits, DefaultJpgScale, false, avifencPath, ultrahdrPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error [{e.FullPath}]: {ex.Message}");
            }
        };

        System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
        return 0;
    }

    // ---- Explorer context menu integration (HKCU, no admin required) ----

    private static (string Key, string Label, string Args)[] MenuEntries =>
    [
        ("HdrShotJpeg", "转换为 HDR JPEG (分享)", "-f jpeg"),
        ("HdrShotAll", "转换为 HDR 全部格式 (JPEG+PNG+AVIF)", "-f all"),
    ];

    private static int InstallMenus()
    {
        string self = Path.Combine(AppContext.BaseDirectory, "hdrshot.exe");
        if (!File.Exists(self)) self = Environment.ProcessPath ?? "hdrshot.exe";
        foreach (var (key, label, args) in MenuEntries)
        {
            RunReg($"add \"HKCU\\Software\\Classes\\SystemFileAssociations\\.jxr\\shell\\{key}\" /ve /t REG_SZ /d \"{label}\" /f");
            RunReg($"add \"HKCU\\Software\\Classes\\SystemFileAssociations\\.jxr\\shell\\{key}\\command\" /ve /t REG_SZ /d \"\\\"{self}\\\" \\\"%1\\\" {args}\" /f");
            Console.WriteLine($"installed: {label} (.jxr)");
        }
        Console.WriteLine("done — right-click a .jxr file to use");
        return 0;
    }

    private static int UninstallMenus()
    {
        foreach (var (key, _, _) in MenuEntries)
        {
            RunReg($"delete \"HKCU\\Software\\Classes\\SystemFileAssociations\\.jxr\\shell\\{key}\" /f");
            Console.WriteLine($"removed: {key}");
        }
        return 0;
    }

    private static void RunReg(string arguments)
    {
        var psi = new ProcessStartInfo("reg.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start reg.exe");
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"reg.exe failed: {p.StandardError.ReadToEnd().Trim()}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            hdrshot — convert Windows/NVIDIA HDR screenshots (.jxr, scRGB float) to shareable HDR formats

            usage: hdrshot <files or wildcards...> [options]

              double-click hdrshot.exe            -> opens the drag-drop GUI
              drag .jxr files onto hdrshot.exe    -> converts (console pauses at the end)
              hdrshot screenshot.jxr              -> CLI conversion (UltraHDR JPEG)

            outputs (all keep the full HDR brightness of the original):
              UltraHDR JPEG  SDR-compatible base + gain map — shows HDR in Chrome/Edge/
                             Safari 26/Android 14+, falls back to normal JPEG elsewhere
                             (default; best for sharing)
              HDR PNG        16-bit, BT.2020 + PQ, cICP + cLLi (lossless-ish archive)
              HDR AVIF       10-bit PQ BT.2020 (tiny, for web/technical audience)

            options:
              -f jpeg|png|avif|all   output format (default: jpeg)
              -o <dir>               output directory (default: same folder as input)
              -q <0-100>             AVIF quality (default 60)
              --sdr-white <nits>     SDR white level for the UltraHDR base image
                                     (default: auto = 2× image MaxFALL, clamped 203–1000)
              --jpg-peak <nits>      UltraHDR JPEG highlight compression: soft knee above
                                     peak/4 nits, rolling off toward this peak (default 300;
                                     0 = off, keeps absolute HDR brightness)
              --jpg-scale <factor>   UltraHDR JPEG overall luminance scale (default 1.0;
                                     e.g. 0.75 darkens midtones too, not just highlights)
              --jpg-flat             build the SDR base from the processed HDR intent, so the
                                     gain map stays ~1.0 and shadows render identically in
                                     every viewer (no reliance on <1 gain-map darkening)
              --avifenc <path>       path to avifenc.exe (otherwise tools\avifenc.exe or PATH)
              --ultrahdr <path>      path to ultrahdr_app.exe (otherwise tools\)
              --watch <dir>          watch a folder and auto-convert new .jxr files
              --gui                  open the drag-drop GUI
              --install-menus        add "转换为 HDR" right-click menu for .jxr files (HKCU)
              --uninstall-menus      remove the right-click menu entries

            examples:
              hdrshot screenshot.jxr                  # -> screenshot.jpg (UltraHDR)
              hdrshot *.jxr -f all -o D:\share
              hdrshot --install-menus

            source formats: 128bppRGBAFloat (NVIDIA FP32) / 64bppRGBAHalf (Windows FP16) and other scRGB float variants
            """);
    }
}
