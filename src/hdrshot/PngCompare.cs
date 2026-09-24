using System;
using System.Runtime.InteropServices;

namespace HdrShot;

/// <summary>
/// Verification helper: decodes two 16-bit RGB PNGs via WIC (converting to 64bppRGBA)
/// and reports max/avg channel difference — used to cross-validate against ledoge/jxr_to_png output.
/// </summary>
internal static class PngCompare
{
    private static readonly Guid Fmt64bppRGBA = new("6fddc324-4e03-4bfe-b185-3d77768dc916"); // GUID_WICPixelFormat64bppRGBA

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("3b16811b-6a43-4ec9-a813-3d930c13b940")]
    private interface IWICBitmapFrameDecodeForConvert
    {
        void GetSize(out uint w, out uint h);
        void GetPixelFormat(out Guid fmt);
        void GetResolution(out double dx, out double dy);
        void CopyPalette(IntPtr pal);
        void CopyPixels(IntPtr rc, uint stride, uint bufSize, IntPtr buffer);
    }

    // IWICFormatConverter : IWICBitmapSource — base methods come FIRST in the vtable, then Initialize/CanConvert.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94")]
    private interface IWICFormatConverter
    {
        // IWICBitmapSource prefix
        void GetSize(out uint w, out uint h);
        void GetPixelFormat(out Guid fmt);
        void GetResolution(out double dx, out double dy);
        void CopyPalette(IntPtr pal);
        void CopyPixels(IntPtr rc, uint stride, uint bufSize, IntPtr buffer);
        // own methods
        void Initialize(
            [MarshalAs(UnmanagedType.Interface)] IWICBitmapFrameDecodeForConvert source,
            ref Guid dstFormat,
            uint dither,
            IntPtr palette,
            double alphaThresholdPercent,
            uint paletteTranslate);
    }

    public static int Run(string pathA, string pathB)
    {
        var a = Decode64bpp(pathA);
        var b = Decode64bpp(pathB);
        if (a.Width != b.Width || a.Height != b.Height)
        {
            Console.Error.WriteLine($"compare: size mismatch {a.Width}x{a.Height} vs {b.Width}x{b.Height}");
            return 1;
        }
        if (a.Data.Length != b.Data.Length)
        {
            Console.Error.WriteLine("compare: buffer length mismatch (color format difference?)");
            return 1;
        }

        long maxDiff = 0, sumDiff = 0, count = 0;
        var hist = new long[9]; // diff buckets: 0, 1, 2-3, 4-7, 8-15, 16-31, 32-63, 64-127, 128+
        for (long i = 0; i < a.Data.Length; i++)
        {
            int d = Math.Abs(a.Data[i] - b.Data[i]);
            if (d > maxDiff) maxDiff = d;
            sumDiff += d;
            if (d != 0) count++;
            hist[d switch { 0 => 0, 1 => 1, <= 3 => 2, <= 7 => 3, <= 15 => 4, <= 31 => 5, <= 63 => 6, <= 127 => 7, _ => 8 }]++;
        }

        double total = a.Data.Length;
        Console.WriteLine($"compare {Path.GetFileName(pathA)} vs {Path.GetFileName(pathB)}: {a.Width}x{a.Height}");
        Console.WriteLine($"  max channel diff = {maxDiff}/65535 ({maxDiff / 65535.0 * 100:F3}%), " +
                          $"avg = {sumDiff / total:F4}, changed samples = {count / total * 100:F2}%");
        Console.WriteLine($"  hist (0 | 1 | 2-3 | 4-7 | 8-15 | 16-31 | 32-63 | 64-127 | 128+): {string.Join(' ', hist)}");
        return 0;
    }

    private static (uint Width, uint Height, ushort[] Data) Decode64bpp(string path)
    {
        Type factoryType = Type.GetTypeFromCLSID(new Guid("317d06e8-5f24-433d-bdf7-79ce68d8abc2"))
            ?? throw new DllNotFoundException("WIC2 factory unresolvable");
        object factoryObj = Activator.CreateInstance(factoryType)
            ?? throw new DllNotFoundException("cannot create WIC2 factory");

        object decoderObj;
        try
        {
            var fa = (IWICImagingFactoryA)factoryObj;
            fa.CreateDecoderFromFilename(path, IntPtr.Zero, 0x80000000u, 0u, out decoderObj);
        }
        catch (InvalidCastException)
        {
            var fb = (IWICImagingFactoryB)factoryObj;
            fb.CreateDecoderFromFilename(path, IntPtr.Zero, 0x80000000u, 0u, out decoderObj);
        }

        var decoder = (IWICBitmapDecoderForCompare)decoderObj;
        decoder.GetFrame(0, out var frame);

        Guid fmt64 = Fmt64bppRGBA;
        var converter = CreateConverter(factoryObj, frame, ref fmt64);

        converter.GetSize(out uint w, out uint h);
        uint stride = w * 8;
        var buf = new byte[(long)w * h * 8];
        GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            converter.CopyPixels(IntPtr.Zero, stride, (uint)buf.Length, pin.AddrOfPinnedObject());
        }
        finally
        {
            pin.Free();
        }

        var data = new ushort[buf.Length / 2];
        Buffer.BlockCopy(buf, 0, data, 0, buf.Length);
        return (w, h, data);
    }

    private static IWICFormatConverter CreateConverter(object factoryObj, IWICBitmapFrameDecodeForConvert frame, ref Guid fmt64)
    {
        // factory.GetFormatConverter (vtable slot 7 of IWICImagingFactory)
        var fac = (IWICImagingFactoryConvert)factoryObj;
        fac.CreateFormatConverter(out object convObj);
        var conv = (IWICFormatConverter)convObj;
        conv.Initialize(frame, ref fmt64, 0 /* dither none */, IntPtr.Zero, 0.0, 0 /* transform none */);
        return conv;
    }

    // IWICImagingFactory prefix up to CreateFormatConverter (slot 7):
    // CreateDecoderFromFilename, CreateDecoderFromStream, CreateDecoderFromFileHandle,
    // CreateComponentInfo, CreateDecoder, CreateEncoder, CreateFormatConverter
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
    private interface IWICImagingFactoryConvert
    {
        void CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string f, IntPtr vendor, uint access, uint options, [MarshalAs(UnmanagedType.Interface)] out object d);
        void CreateDecoderFromStream(IntPtr stream, IntPtr vendor, uint options, [MarshalAs(UnmanagedType.Interface)] out object d);
        void CreateDecoderFromFileHandle(IntPtr handle, IntPtr vendor, uint options, [MarshalAs(UnmanagedType.Interface)] out object d);
        void CreateComponentInfo(ref Guid clsid, [MarshalAs(UnmanagedType.Interface)] out object info);
        void CreateDecoder(ref Guid container, IntPtr vendor, [MarshalAs(UnmanagedType.Interface)] out object d);
        void CreateEncoder(ref Guid container, IntPtr vendor, [MarshalAs(UnmanagedType.Interface)] out object e);
        void CreatePalette(IntPtr palette); // slot 7 — required to keep CreateFormatConverter in its true vtable position
        void CreateFormatConverter([MarshalAs(UnmanagedType.Interface)] out object converter);
    }

    // Decoder prefix up to GetFrame (slot 11) for compare path
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf")]
    private interface IWICBitmapDecoderForCompare
    {
        void QueryCapability(IntPtr stream, out uint cap);
        void Initialize(IntPtr stream, uint options);
        void GetContainerFormat(out Guid fmt);
        void GetDecoderInfo([MarshalAs(UnmanagedType.Interface)] out object info);
        void CopyPalette(IntPtr pal);
        void GetMetadataQueryReader([MarshalAs(UnmanagedType.Interface)] out object reader);
        void GetPreview([MarshalAs(UnmanagedType.Interface)] out object preview);
        void GetColorContexts(uint cCount, IntPtr contexts, out uint actual);
        void GetThumbnail([MarshalAs(UnmanagedType.Interface)] out object thumb);
        void GetFrameCount(out uint count);
        void GetFrame(uint index, [MarshalAs(UnmanagedType.Interface)] out IWICBitmapFrameDecodeForConvert frame);
    }
}
