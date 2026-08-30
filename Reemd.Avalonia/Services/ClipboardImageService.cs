using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;

namespace Reemd.Services;

/// <summary>
/// Platform-specific clipboard image reading and writing via native interop.
/// All methods return/accept PNG bytes for cross-platform consistency.
/// </summary>
public static class ClipboardImageService
{
    // Windows clipboard format constants
    private const uint CF_BITMAP = 2;
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>Raised for diagnostic log messages. Subscribe from the UI layer.</summary>
    public static event Action<string>? LogMessage;

    private static void Log(string message) => LogMessage?.Invoke($"[ClipboardImage] {message}");

    /// <summary>
    /// Reads the current clipboard image as PNG bytes, or returns null if no image is present.
    /// </summary>
    public static async Task<byte[]?> ReadImageAsync()
    {
        if (OperatingSystem.IsMacOS()) return await ReadMacImageAsync();
        if (OperatingSystem.IsWindows()) return ReadWindowsImageAsync();
        Log("Unsupported platform for image read");
        return null;
    }

    /// <summary>
    /// Writes PNG bytes to the system clipboard as an image.
    /// </summary>
    public static async Task WriteImageAsync(byte[] pngData)
    {
        Log($"Writing {pngData.Length} bytes to clipboard");
        if (OperatingSystem.IsMacOS()) await WriteMacImageAsync(pngData);
        else if (OperatingSystem.IsWindows()) WriteWindowsImageAsync(pngData);
        else Log("Unsupported platform for image write");
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
                if (pngData != null && pngData.Length > 0)
                {
                    Log($"Read PNG from pasteboard: {pngData.Length} bytes");
                    return pngData;
                }

                Log("No PNG on pasteboard, trying TIFF...");
                // Fall back to TIFF (macOS screenshots are TIFF)
                var tiffType = CreateNSString("public.tiff");
                var tiffData = ReadPasteboardData(generalPb, tiffType);
                if (tiffData == null || tiffData.Length == 0)
                {
                    Log("No image data on pasteboard (neither PNG nor TIFF)");
                    return null;
                }

                Log($"Read TIFF from pasteboard: {tiffData.Length} bytes, converting to PNG...");
                // Convert TIFF → PNG using CoreGraphics ImageIO
                var pngResult = ConvertTiffToPng(tiffData);
                if (pngResult != null)
                    Log($"TIFF→PNG conversion succeeded: {pngResult.Length} bytes");
                else
                    Log("TIFF→PNG conversion returned null");
                return pngResult;
            }
            catch (Exception ex)
            {
                Log($"macOS read error: {ex.GetType().Name}: {ex.Message}");
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
                Log($"macOS: wrote {pngData.Length} bytes as public.png");
            }
            catch (Exception ex)
            {
                Log($"macOS write error: {ex.GetType().Name}: {ex.Message}");
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
        var dataSel = sel_registerName("dataForType:");
        var nsData = objc_msgSend_nint_nint(pasteboard, dataSel, typeString);
        if (nsData == 0) return null;

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
        var tiffNsData = CreateNSData(tiffData);
        if (tiffNsData == 0) return null;

        var source = CGImageSourceCreateWithData(tiffNsData, 0);
        if (source == 0) return null;

        try
        {
            var image = CGImageSourceCreateImageAtIndex(source, 0, 0);
            if (image == 0) return null;

            try
            {
                var pngOutputNsData = objc_msgSend_intptr(
                    objc_getClass("NSMutableData"),
                    sel_registerName("data"));
                if (pngOutputNsData == 0) return null;

                var kUTTypePng = CFStringCreateWithCString(0, "public.png", 0x08000100);

                try
                {
                    var dest = CGImageDestinationCreateWithData(pngOutputNsData, kUTTypePng, 1, 0);
                    if (dest == 0) return null;

                    try
                    {
                        CGImageDestinationAddImage(dest, image, 0);

                        if (!CGImageDestinationFinalize(dest)) return null;

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

    // Objective-C runtime P/Invoke
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

    #region Windows — Win32 clipboard + GDI+ interop

    // GDI+ state — initialized lazily, shut down on process exit
    private static uint _gdiplusToken;
    private static bool _gdiplusInitialized;

    private static void EnsureGdiplusInitialized()
    {
        if (_gdiplusInitialized) return;
        try
        {
            var input = new GdiplusStartupInput { GdiplusVersion = 1 };
            var status = GdiplusStartup(out _gdiplusToken, ref input, out _);
            _gdiplusInitialized = status == 0; // Ok
            if (!_gdiplusInitialized)
                Log($"GdiplusStartup failed with status {status}");
            else
                Log("GDI+ initialized successfully");
        }
        catch (Exception ex)
        {
            Log($"GDI+ init error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static byte[]? ReadWindowsImageAsync()
    {
        try
        {
            // Enumerate available clipboard formats for diagnostics
            Log("Checking clipboard formats...");
            uint fmtIdx = 0;
            var availableFormats = new List<string>();
            while (true)
            {
                fmtIdx = EnumClipboardFormats(fmtIdx);
                if (fmtIdx == 0) break;
                var nameBuf = new char[256];
                var nameLen = GetClipboardFormatNameW(fmtIdx, nameBuf, 256);
                if (nameLen > 0)
                    availableFormats.Add($"{fmtIdx}:{new string(nameBuf, 0, nameLen)}");
                else
                    availableFormats.Add($"{fmtIdx}:standard");
            }
            Log($"Available formats: {string.Join(", ", availableFormats)}");

            // Try CF_PNG first — some screenshot tools provide this
            var cfPng = RegisterClipboardFormatW("PNG");
            Log($"CF_PNG format ID: {cfPng}");
            if (cfPng != 0)
            {
                var hMemPng = GetClipboardData(cfPng);
                Log($"GetClipboardData(CF_PNG) = {(hMemPng == IntPtr.Zero ? "null" : "valid")}");
                if (hMemPng != IntPtr.Zero)
                {
                    var result = CopyGlobalMem(hMemPng);
                    if (result != null)
                    {
                        Log($"Read CF_PNG: {result.Length} bytes");
                        return result;
                    }
                }
            }

            // Fall back to CF_DIB and convert to PNG
            Log("Trying CF_DIB...");
            var hMemDib = GetClipboardData(CF_DIB);
            Log($"GetClipboardData(CF_DIB) = {(hMemDib == IntPtr.Zero ? "null" : "valid")}");
            if (hMemDib == IntPtr.Zero) return null;

            var dib = CopyGlobalMem(hMemDib);
            if (dib == null)
            {
                Log("CF_DIB: failed to read global memory");
                return null;
            }

            Log($"CF_DIB: read {dib.Length} bytes");
            if (dib.Length < 40)
            {
                Log("CF_DIB: data too small for BITMAPINFOHEADER");
                return null;
            }

            // Log DIB header details
            var biSize = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(0));
            var biWidth = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4));
            var biHeight = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8));
            var biBitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
            Log($"DIB header: size={biSize}, width={biWidth}, height={biHeight}, bpp={biBitCount}");

            var pngResult = ConvertDibToPng(dib);
            if (pngResult != null)
                Log($"DIB→PNG conversion succeeded: {pngResult.Length} bytes");
            else
                Log("DIB→PNG conversion returned null");
            return pngResult;
        }
        catch (Exception ex)
        {
            Log($"Windows read error: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void WriteWindowsImageAsync(byte[] pngData)
    {
        try
        {
            Log($"Writing {pngData.Length} bytes to Windows clipboard");

            if (!OpenClipboard(IntPtr.Zero))
            {
                Log("OpenClipboard failed");
                return;
            }

            try
            {
                EmptyClipboard();
                int formatsSet = 0;

                // 1. Write CF_PNG — supported by modern apps (Chrome, Edge, etc.)
                var cfPng = RegisterClipboardFormatW("PNG");
                if (cfPng != 0)
                {
                    var hMemPng = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)pngData.Length);
                    if (hMemPng != IntPtr.Zero)
                    {
                        var ptr = GlobalLock(hMemPng);
                        if (ptr != IntPtr.Zero)
                        {
                            try { Marshal.Copy(pngData, 0, ptr, pngData.Length); }
                            finally { GlobalUnlock(hMemPng); }

                            SetClipboardData(cfPng, hMemPng);
                            formatsSet++;
                            Log($"Set CF_PNG ({pngData.Length} bytes)");
                        }
                        else
                            GlobalFree(hMemPng);
                    }
                }

                // 2. Write CF_BITMAP via GDI+ — supported by all Windows apps (Word, Paint, etc.)
                EnsureGdiplusInitialized();
                if (_gdiplusInitialized)
                {
                    var hBitmap = LoadPngAsHBitmap(pngData);
                    if (hBitmap != IntPtr.Zero)
                    {
                        SetClipboardData(CF_BITMAP, hBitmap);
                        formatsSet++;
                        Log("Set CF_BITMAP via GDI+");
                    }
                    else
                        Log("Failed to create HBITMAP from PNG");
                }

                Log($"Clipboard write complete: {formatsSet} format(s) set");
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            Log($"Windows write error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Uses GDI+ to decode PNG bytes into a GDI HBITMAP.
    /// The caller must NOT free the returned handle — the clipboard takes ownership.
    /// </summary>
    private static IntPtr LoadPngAsHBitmap(byte[] pngData)
    {
        // Create an IStream over the PNG bytes
        var hGlobal = Marshal.AllocHGlobal(pngData.Length);
        try
        {
            Marshal.Copy(pngData, 0, hGlobal, pngData.Length);
        }
        catch
        {
            Marshal.FreeHGlobal(hGlobal);
            return IntPtr.Zero;
        }

        var hr = CreateStreamOnHGlobal(hGlobal, false, out var stream);
        if (hr != 0 || stream == null)
        {
            Marshal.FreeHGlobal(hGlobal);
            Log($"CreateStreamOnHGlobal failed: 0x{hr:X8}");
            return IntPtr.Zero;
        }

        try
        {
            // Load PNG as GDI+ image
            var status = GdipLoadImageFromStream(stream, out var image);
            if (status != 0 || image == IntPtr.Zero)
            {
                Log($"GdipLoadImageFromStream failed: status={status}");
                return IntPtr.Zero;
            }

            try
            {
                // Get image dimensions for diagnostics
                GdipGetImageWidth(image, out var width);
                GdipGetImageHeight(image, out var height);
                Log($"GDI+ decoded PNG: {width}x{height}");

                // Create HBITMAP (background = 0 = black for transparent pixels)
                status = GdipCreateHBITMAPFromBitmap(image, out var hBitmap, 0);
                if (status != 0 || hBitmap == IntPtr.Zero)
                {
                    Log($"GdipCreateHBITMAPFromBitmap failed: status={status}");
                    return IntPtr.Zero;
                }

                Log($"Created HBITMAP: {hBitmap}");
                return hBitmap;
            }
            finally
            {
                GdipDisposeImage(image);
            }
        }
        finally
        {
            // Release the COM IStream — does NOT free hGlobal (we don't own it after this)
            if (OperatingSystem.IsWindows()) Marshal.ReleaseComObject(stream);
        }
    }

    /// <summary>
    /// Converts raw DIB data (BITMAPINFOHEADER + pixel data) to PNG by creating
    /// a BMP in memory and decoding it with Avalonia's Bitmap.
    /// </summary>
    private static byte[]? ConvertDibToPng(byte[] dib)
    {
        var bfOffBits = 14 + Marshal.SizeOf<BITMAPINFOHEADER>();
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

    // GDI+ types
    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        [MarshalAs(UnmanagedType.Bool)]
        public bool SuppressBackgroundThread;
        [MarshalAs(UnmanagedType.Bool)]
        public bool SuppressExternalCodecs;
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

    // Win32 P/Invoke
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

    [DllImport("user32.dll")]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll")]
    private static extern int GetClipboardFormatNameW(uint format, [Out] char[] lpszFormatName, int nMaxCount);

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

    // COM IStream
    [DllImport("ole32.dll")]
    private static extern int CreateStreamOnHGlobal(IntPtr hGlobal, [MarshalAs(UnmanagedType.Bool)] bool fDeleteOnRelease, [MarshalAs(UnmanagedType.Interface)] out IStream ppstm);

    // GDI+ P/Invoke
    [DllImport("gdiplus.dll")]
    private static extern int GdiplusStartup(out uint token, ref GdiplusStartupInput input, out IntPtr output);

    [DllImport("gdiplus.dll")]
    private static extern void GdiplusShutdown(uint token);

    [DllImport("gdiplus.dll")]
    private static extern int GdipLoadImageFromStream([MarshalAs(UnmanagedType.Interface)] IStream stream, out IntPtr image);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateHBITMAPFromBitmap(IntPtr image, out IntPtr hBitmap, uint background);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDisposeImage(IntPtr image);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageWidth(IntPtr image, out uint width);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageHeight(IntPtr image, out uint height);

    #endregion
}
