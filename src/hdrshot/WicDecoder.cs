using System;
using System.Runtime.InteropServices;

namespace HdrShot;

/// <summary>
/// Decodes a JPEG XR (JXR) file into full-precision scRGB float RGBA pixels via WIC.
/// scRGB semantics: BT.709 primaries, linear transfer, 1.0 == 80 nits, values may exceed 1.0 (HDR)
/// and be negative (out-of-gamut). NVIDIA overlay screenshots are typically FP32 (128bppRGBAFloat),
/// Windows 11 Snipping Tool / Game Bar screenshots are typically FP16 (64bppRGBAHalf).
/// </summary>
internal static class WicDecoder
{
    // ---- pixel format GUIDs (wincodec.idl / wincodec.h) ----
    private static readonly Guid Fmt128bppRGBAFloat = new("6fddc324-4e03-4bfe-b185-3d77768dc919");
    private static readonly Guid Fmt128bppPRGBAFloat = new("6fddc324-4e03-4bfe-b185-3d77768dc91a");
    private static readonly Guid Fmt128bppRGBFloat = new("6fddc324-4e03-4bfe-b185-3d77768dc91b");
    private static readonly Guid Fmt64bppRGBAHalf = new("6fddc324-4e03-4bfe-b185-3d77768dc93a");
    private static readonly Guid Fmt64bppRGBHalf = new("6fddc324-4e03-4bfe-b185-3d77768dc942");
    private static readonly Guid Fmt64bppPRGBAHalf = new("58ad26c2-c623-4d9d-b320-387e49f8c442");
    private static readonly Guid Fmt96bppRGBFloat = new("e3fed78f-e8db-4acf-84c1-e97f6136b327");

    // ---- COM interop ----

    // NOTE: on recent Windows 11 builds the legacy WIC1 factory CLSID (cacaf262-…) is no longer
    // registered (REGDB_E_CLASSNOTREG); WIC2 (317d06e8-…) is always present and its object QIs
    // cleanly to the base IWICImagingFactory interface.
    [ComImport, Guid("317d06e8-5f24-433d-bdf7-79ce68d8abc2")]
    private sealed class CLSIDWICImagingFactory { }

    // Two candidate IIDs for IWICImagingFactory — see WicInterop.cs (namespace-level, shared with PngCompare).

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf")]
    private interface IWICBitmapDecoder
    {
        // full vtable prefix per wincodec.idl — order matters!
        void QueryCapability(IntPtr pIStream, out uint pdwCapability);
        void Initialize(IntPtr pIStream, uint cacheOptions);
        void GetContainerFormat(out Guid pguidContainerFormat);
        void GetDecoderInfo([MarshalAs(UnmanagedType.Interface)] out object ppIDecoderInfo);
        void CopyPalette(IntPtr pIPalette);
        void GetMetadataQueryReader([MarshalAs(UnmanagedType.Interface)] out object ppIMetadataQueryReader);
        void GetPreview([MarshalAs(UnmanagedType.Interface)] out object ppIBitmapSource);
        void GetColorContexts(uint cCount, IntPtr ppIColorContexts, out uint pcActualCount);
        void GetThumbnail([MarshalAs(UnmanagedType.Interface)] out object ppIBitmapSource);
        void GetFrameCount(out uint pCount);
        void GetFrame(uint index, [MarshalAs(UnmanagedType.Interface)] out IWICBitmapFrameDecode ppIFrameDecoder);
    }

    // IWICBitmapFrameDecode : IWICBitmapSource — the five IWICBitmapSource methods come first in the vtable.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("3b16811b-6a43-4ec9-a813-3d930c13b940")]
    private interface IWICBitmapFrameDecode
    {
        void GetSize(out uint puiWidth, out uint puiHeight);
        void GetPixelFormat(out Guid pPixelFormat);
        void GetResolution(out double pdpiX, out double pdpiY);
        void CopyPalette(IntPtr pIPalette);
        void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer);
    }

    private const uint GenericRead = 0x80000000;
    private const uint WicDecodeMetadataCacheOnDemand = 0;

    /// <param name="formatName">human-readable native WIC pixel format name (for diagnostics)</param>
    public static (uint Width, uint Height, string FormatName, float[] Rgba) DecodeFloatRgba(string path)
    {
        // NOTE: `new ComImportClass()` activation can fail in some sandboxed/.NET contexts while
        // Type.GetTypeFromCLSID + Activator.CreateInstance works — use the latter (proven).
        Type factoryType = Type.GetTypeFromCLSID(new Guid("317d06e8-5f24-433d-bdf7-79ce68d8abc2"))
            ?? throw new DllNotFoundException("WIC2 imaging factory CLSID not resolvable");
        object factory = Activator.CreateInstance(factoryType)
            ?? throw new DllNotFoundException("cannot CoCreateInstance WIC2 imaging factory");

        object decoderObj;
        try
        {
            var fa = (IWICImagingFactoryA)factory;
            fa.CreateDecoderFromFilename(path, IntPtr.Zero, GenericRead, WicDecodeMetadataCacheOnDemand, out decoderObj);
        }
        catch (Exception exA)
        {
            Console.Error.WriteLine($"[diag] pathA failed: {exA.GetType().Name} hr=0x{exA.HResult:X8} {exA.Message}");
            try
            {
                var fb = (IWICImagingFactoryB)factory;
                fb.CreateDecoderFromFilename(path, IntPtr.Zero, GenericRead, WicDecodeMetadataCacheOnDemand, out decoderObj);
            }
            catch (Exception exB)
            {
                Console.Error.WriteLine($"[diag] pathB failed: {exB.GetType().Name} hr=0x{exB.HResult:X8} {exB.Message}");
                throw;
            }
        }

        var decoder = (IWICBitmapDecoder)decoderObj;
        decoder.GetFrameCount(out uint frameCount);
        if (frameCount == 0) throw new InvalidDataException("file contains no frames");
        decoder.GetFrame(0, out IWICBitmapFrameDecode frame);

        frame.GetPixelFormat(out Guid fmt);
        frame.GetSize(out uint width, out uint height);
        if (width == 0 || height == 0) throw new InvalidDataException("zero-sized image");

        (int comps, int bytesPerComp, bool premul, string name) = FormatInfo(fmt);

        long byteCount = (long)width * height * comps * bytesPerComp;
        var buf = new byte[byteCount];
        uint stride = (uint)((long)width * comps * bytesPerComp);

        GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(IntPtr.Zero, stride, (uint)byteCount, pin.AddrOfPinnedObject());
        }
        finally
        {
            pin.Free();
        }

        var rgba = new float[checked((long)width * height * 4)];
        long pixels = (long)width * height;
        int srcStride = comps * bytesPerComp;

        if (bytesPerComp == 4)
        {
            for (long p = 0; p < pixels; p++)
            {
                int o = (int)(p * srcStride);
                float a = comps == 4 ? BitConverter.UInt32BitsToSingle(BitConverter.ToUInt32(buf, o + 12)) : 1f;
                float inv = premul && a > 1e-4f ? 1f / a : 1f;
                rgba[p * 4 + 0] = BitConverter.UInt32BitsToSingle(BitConverter.ToUInt32(buf, o + 0)) * inv;
                rgba[p * 4 + 1] = BitConverter.UInt32BitsToSingle(BitConverter.ToUInt32(buf, o + 4)) * inv;
                rgba[p * 4 + 2] = BitConverter.UInt32BitsToSingle(BitConverter.ToUInt32(buf, o + 8)) * inv;
            }
        }
        else // 16-bit half floats
        {
            for (long p = 0; p < pixels; p++)
            {
                int o = (int)(p * srcStride);
                float a = comps == 4 ? (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(buf, o + 6)) : 1f;
                float inv = premul && a > 1e-4f ? 1f / a : 1f;
                rgba[p * 4 + 0] = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(buf, o + 0)) * inv;
                rgba[p * 4 + 1] = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(buf, o + 2)) * inv;
                rgba[p * 4 + 2] = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(buf, o + 4)) * inv;
            }
        }

        return (width, height, name, rgba);
    }

    private static (int Comps, int BytesPerComp, bool Premultiplied, string Name) FormatInfo(Guid fmt)
    {
        if (fmt.Equals(Fmt128bppRGBAFloat)) return (4, 4, false, "128bppRGBAFloat (scRGB FP32 — NVIDIA-style)");
        if (fmt.Equals(Fmt128bppPRGBAFloat)) return (4, 4, true, "128bppPRGBAFloat (scRGB FP32, premultiplied)");
        if (fmt.Equals(Fmt128bppRGBFloat)) return (3, 4, false, "128bppRGBFloat (scRGB FP32)");
        if (fmt.Equals(Fmt64bppRGBAHalf)) return (4, 2, false, "64bppRGBAHalf (scRGB FP16 — Windows screenshot-style)");
        if (fmt.Equals(Fmt64bppRGBHalf)) return (3, 2, false, "64bppRGBHalf (scRGB FP16)");
        if (fmt.Equals(Fmt64bppPRGBAHalf)) return (4, 2, true, "64bppPRGBAHalf (scRGB FP16, premultiplied)");
        if (fmt.Equals(Fmt96bppRGBFloat)) return (3, 4, false, "96bppRGBFloat (scRGB FP32)");
        throw new NotSupportedException($"not a scRGB float pixel format: {fmt:B}");
    }
}
