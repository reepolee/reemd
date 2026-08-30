using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Reemd.Services;

/// <summary>
/// Platform-specific clipboard image reading and writing via native interop.
/// All methods return/accept PNG bytes for cross-platform consistency.
/// </summary>
public static class ClipboardImageService
{
    // Windows clipboard formats
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>
    /// Reads the current clipboard image as PNG bytes, or returns null if no image is present.
    /// </summary>
    public static async Task<byte[]?> ReadImageAsync()
    {
        if (OperatingSystem.IsMacOS()) return await ReadMacImageAsync();
        if (OperatingSystem.IsWindows()) return ReadWindowsImageAsync();
        return null;
    }

    /// <summary>
    /// Writes PNG bytes to the system clipboard as an image.
    /// </summary>
    public static async Task WriteImageAsync(byte[] pngData)
    {
        if (OperatingSystem.IsMacOS()) await WriteMacImageAsync(pngData);
        else if (OperatingSystem.IsWindows()) WriteWindowsImageAsync(pngData);
    }

    /// <summary>
    /// Computes a stable hash of clipboard image bytes for deduplication.
    /// </summary>
    public static string ComputeImageHash(byte[] imageData)
    {
        var hash = SHA256.HashData(imageData);
        return Convert.ToBase64String(hash);
    }

    #region macOS — NSPasteboard + ImageIO interop

    private static async Task<byte[]?> ReadMacImageAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                var generalPb = GetGeneralPasteboard();

                // Try PNG first
                var pngType = CreateNSString("public.png");
                var pngData = ReadPasteboardData(generalPb, pngType);
                if (pngData != null && pngData.Length > 0) return pngData;

                // Fall back to TIFF (macOS screenshots are TIFF)
                var tiffType = CreateNSString("public.tiff");
                var tiffData = ReadPasteboardData(generalPb, tiffType);
                if (tiffData == null || tiffData.Length == 0) return null;

                // Convert TIFF → PNG using CoreGraphics ImageIO
                return ConvertTiffToPng(tiffData);
            }
            catch
            {
                return null;
            }
        }).ConfigureAwait(false);
    }

    private static async Task WriteMacImageAsync(byte[] pngData)
    {
        await Task.Run(() =>
        {
            try
            {
                var generalPb = GetGeneralPasteboard();

                // Clear the pasteboard
                objc_msgSend_intptr(generalPb, sel_registerName("clearContents"));

                // Create NSData from PNG bytes
                var nsData = CreateNSData(pngData);

                // Create NSString for "public.png"
                var pngType = CreateNSString("public.png");

                // setData:forType:
                var setDataSel = sel_registerName("setData:forType:");
                objc_msgSend_nint_nint_nint(generalPb, setDataSel, nsData, pngType);
            }
            catch
            {
                // Best-effort — swallow errors
            }
        }).ConfigureAwait(false);
    }

    private static nint GetGeneralPasteboard()
    {
        var pbClass = objc_getClass("NSPasteboard");
        return objc_msgSend_intptr(pbClass, sel_registerName("generalPasteboard"));
    }

    private static byte[]? ReadPasteboardData(nint pasteboard, nint typeString)
    {
        // dataForType:
        var dataSel = sel_registerName("dataForType:");
        var nsData = objc_msgSend_nint_nint(pasteboard, dataSel, typeString);
        if (nsData == 0) return null;

        // NSData → byte[]
        return NsDataToBytes(nsData);
    }

    private static byte[]? NsDataToBytes(nint nsData)
    {
        var lengthSel = sel_registerName("length");
        var length = (int)objc_msgSend_intptr(nsData, lengthSel);
        if (length <= 0) return null;

        var bytesSel = sel_registerName("bytes");
        var bytesPtr = objc_msgSend_intptr(nsData, bytesSel);
        if (bytesPtr == 0) return null;

        var result = new byte[length];
        Marshal.Copy(bytesPtr, result, 0, length);
        return result;
    }

    private static nint CreateNSData(byte[] data)
    {
        var dataClass = objc_getClass("NSData");
        var dataWithBytesSel = sel_registerName("dataWithBytes:length:");
        var ptr = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
            return objc_msgSend_nint_nint_nint(dataClass, dataWithBytesSel, ptr, (nint)data.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static nint CreateNSString(string str)
    {
        var nsStringClass = objc_getClass("NSString");
        var stringWithUtf8Sel = sel_registerName("stringWithUTF8String:");
        // objc_msgSend passes the char* as a pointer (nint)
        var ptr = Marshal.StringToHGlobalAnsi(str);
        try
        {
            return objc_msgSend_nint_nint(nsStringClass, stringWithUtf8Sel, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static byte[]? ConvertTiffToPng(byte[] tiffData)
    {
        // Use CoreGraphics ImageIO to convert TIFF → PNG natively
        var tiffNsData = CreateNSData(tiffData);
        if (tiffNsData == 0) return null;

        // CGImageSourceCreateWithData(tiffData, NULL)
        var source = CGImageSourceCreateWithData(tiffNsData, 0);
        if (source == 0) return null;

        try
        {
            // CGImageSourceCreateImageAtIndex(source, 0, NULL)
            var image = CGImageSourceCreateImageAtIndex(source, 0, 0);
            if (image == 0) return null;

            try
            {
                // Create mutable NSData for PNG output
                var pngOutputNsData = objc_msgSend_intptr(
                    objc_getClass("NSMutableData"),
                    sel_registerName("data"));
                if (pngOutputNsData == 0) return null;

                // kUTTypePNG = "public.png" as CFString
                var kUTTypePng = CFStringCreateWithCString(0, "public.png", 0x08000100);

                try
                {
                    // CGImageDestinationCreateWithData(output, kUTTypePNG, 1, NULL)
                    var dest = CGImageDestinationCreateWithData(pngOutputNsData, kUTTypePng, 1, 0);
                    if (dest == 0) return null;

                    try
                    {
                        // CGImageDestinationAddImage(dest, image, NULL)
                        CGImageDestinationAddImage(dest, image, 0);

                        // CGImageDestinationFinalize(dest)
                        if (!CGImageDestinationFinalize(dest)) return null;

                        // Read PNG bytes from the output NSMutableData
                        return NsDataToBytes(pngOutputNsData);
                    }
                    finally
                    {
                        CFRelease(dest);
                    }
                }
                finally
                {
                    CFRelease(kUTTypePng);
                }
            }
            finally
            {
                CFRelease(image);
            }
        }
        finally
        {
            CFRelease(source);
        }
    }

    // CoreGraphics / ImageIO / CoreFoundation P/Invoke
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern IntPtr CGImageSourceCreateWithData(IntPtr data, IntPtr options);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern IntPtr CGImageSourceCreateImageAtIndex(IntPtr source, nuint index, IntPtr options);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern IntPtr CGImageDestinationCreateWithData(IntPtr data, IntPtr type, nuint count, IntPtr options);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern void CGImageDestinationAddImage(IntPtr destination, IntPtr image, IntPtr properties);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern bool CGImageDestinationFinalize(IntPtr destination);

    // Objective-C runtime P/Invoke (shared with MacClipboardChangeMonitor)
    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_intptr(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_nint_nint(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_nint_nint_nint(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    #endregion

    #region Windows — Win32 clipboard API

    private static byte[]? ReadWindowsImageAsync()
    {
        try
        {
            // Try CF_PNG first — most modern Windows screenshot tools provide this
            var cfPng = RegisterClipboardFormatW("PNG");
            if (cfPng != 0)
            {
                var hMemPng = GetClipboardData(cfPng);
                if (hMemPng != IntPtr.Zero)
                {
                    var result = CopyGlobalMem(hMemPng);
                    if (result != null) return result;
                }
            }

            // Fall back to CF_DIB and convert to PNG
            var hMemDib = GetClipboardData(CF_DIB);
            if (hMemDib == IntPtr.Zero) return null;

            var dib = CopyGlobalMem(hMemDib);
            if (dib == null || dib.Length < 40) return null; // minimum BITMAPINFOHEADER size

            return ConvertDibToPng(dib);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteWindowsImageAsync(byte[] pngData)
    {
        try
        {
            var cfPng = RegisterClipboardFormatW("PNG");
            if (cfPng == 0) return;

            var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)pngData.Length);
            if (hMem == IntPtr.Zero) return;

            var ptr = GlobalLock(hMem);
            if (ptr == IntPtr.Zero)
            {
                GlobalFree(hMem);
                return;
            }

            try
            {
                Marshal.Copy(pngData, 0, ptr, pngData.Length);
            }
            finally
            {
                GlobalUnlock(hMem);
            }

            if (!OpenClipboard(IntPtr.Zero))
            {
                GlobalFree(hMem);
                return;
            }

            try
            {
                EmptyClipboard();
                SetClipboardData(cfPng, hMem);
                // hMem is now owned by the clipboard — do NOT free it
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch
        {
            // Best-effort — swallow errors
        }
    }

    /// <summary>
    /// Converts raw DIB data (BITMAPINFOHEADER + pixel data) to PNG by creating
    /// a BMP in memory and decoding it with Avalonia's Bitmap.
    /// </summary>
    private static byte[]? ConvertDibToPng(byte[] dib)
    {
        // DIB starts with BITMAPINFOHEADER. Prepend a 14-byte BITMAPFILEHEADER
        // to create a valid BMP that Avalonia's Bitmap can decode.
        var bfOffBits = 14 + Marshal.SizeOf<BITMAPINFOHEADER>();
        // biSize is at offset 0 of the DIB header (4 bytes LE)
        var biSize = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(0));
        if (biSize > 0 && biSize < dib.Length)
            bfOffBits = 14 + biSize;

        var bmpSize = 14 + dib.Length;
        var bmp = new byte[bmpSize];

        // BITMAPFILEHEADER
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmpSize);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), bfOffBits);

        // Copy DIB (BITMAPINFOHEADER + pixel data)
        Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);

        // Decode BMP → Bitmap → encode as PNG
        using var bmpStream = new MemoryStream(bmp);
        var bitmap = new Avalonia.Media.Imaging.Bitmap(bmpStream);
        using var pngStream = new MemoryStream();
#pragma warning disable CS0618
        bitmap.Save(pngStream);
#pragma warning restore CS0618
        return pngStream.ToArray();
    }

    private static byte[]? CopyGlobalMem(IntPtr hMem)
    {
        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero) return null;

        try
        {
            var size = (int)GlobalSize(hMem);
            if (size <= 0) return null;
            var data = new byte[size];
            Marshal.Copy(ptr, data, 0, size);
            return data;
        }
        finally
        {
            GlobalUnlock(hMem);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    #endregion
}
