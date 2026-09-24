using System;
using System.Runtime.InteropServices;

namespace HdrShot;

/// <summary>
/// IWICImagingFactory interop, shared by WicDecoder and PngCompare.
/// Two candidate IIDs: mingw-w64/winSDK idl both list 54d7a935ff70 for IWICImagingFactory;
/// a second older candidate (35d1c17ad0cd) is kept as runtime fallback.
/// </summary>
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
internal interface IWICImagingFactoryA
{
    void CreateDecoderFromFilename(
        [MarshalAs(UnmanagedType.LPWStr)] string wzFilename,
        IntPtr pguidVendor,
        uint dwDesiredAccess,
        uint dwMetadataOptions,
        [MarshalAs(UnmanagedType.Interface)] out object ppIDecoder);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("ec5ec8a9-c395-4314-9c77-35d1c17ad0cd")]
internal interface IWICImagingFactoryB
{
    void CreateDecoderFromFilename(
        [MarshalAs(UnmanagedType.LPWStr)] string wzFilename,
        IntPtr pguidVendor,
        uint dwDesiredAccess,
        uint dwMetadataOptions,
        [MarshalAs(UnmanagedType.Interface)] out object ppIDecoder);
}
