using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Reemd.Services;

/// <summary>
/// Places an eager PNG and CF_DIB image on the Windows clipboard.
/// </summary>
public static class WindowsClipboardImageWriter
{
    private const uint CfDib = 8;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;
    private const int BitmapInfoHeaderSize = 40;
    private const int ClipboardOpenAttempts = 10;
    private const int ClipboardOpenRetryMs = 20;
    private const int Dpi96PixelsPerMeter = 3780;

    public static async Task WriteAsync(nint owner_handle, byte[] png_data, Bitmap bitmap)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        if (owner_handle == nint.Zero)
            throw new ArgumentException("A native owner window is required.", nameof(owner_handle));

        var png_format = RegisterClipboardFormat("PNG");
        if (png_format == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot register the PNG clipboard format.");

        var dib_data = CreateDib(bitmap);
        var png_handle = AllocateClipboardMemory(png_data);
        var dib_handle = AllocateClipboardMemory(dib_data);
        var clipboard_open = false;

        try
        {
            for (var attempt = 1; attempt <= ClipboardOpenAttempts; attempt++)
            {
                clipboard_open = OpenClipboard(owner_handle);
                if (clipboard_open) break;

                if (attempt < ClipboardOpenAttempts)
                    await Task.Delay(ClipboardOpenRetryMs);
            }

            if (!clipboard_open)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the Windows clipboard.");
            if (!EmptyClipboard())
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot take ownership of the Windows clipboard.");

            var stored_png_handle = SetClipboardData(png_format, png_handle);
            if (stored_png_handle == nint.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot set the PNG clipboard format.");
            png_handle = nint.Zero;

            var stored_dib_handle = SetClipboardData(CfDib, dib_handle);
            if (stored_dib_handle == nint.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot set the CF_DIB clipboard format.");
            dib_handle = nint.Zero;
        }
        finally
        {
            if (clipboard_open)
                CloseClipboard();
            if (png_handle != nint.Zero)
                GlobalFree(png_handle);
            if (dib_handle != nint.Zero)
                GlobalFree(dib_handle);
        }
    }

    private static byte[] CreateDib(Bitmap bitmap)
    {
        var pixel_format = bitmap.Format;
        if (pixel_format != PixelFormats.Bgra8888 && pixel_format != PixelFormats.Rgba8888)
            throw new InvalidDataException($"Unsupported Windows clipboard pixel format: {pixel_format}.");

        var pixel_size = bitmap.PixelSize;
        var stride = checked(pixel_size.Width * 4);
        var pixel_byte_count = checked(stride * pixel_size.Height);
        var source_pixels = new byte[pixel_byte_count];
        var pixel_buffer = Marshal.AllocHGlobal(pixel_byte_count);

        try
        {
            var pixel_rect = new PixelRect(pixel_size);
            bitmap.CopyPixels(pixel_rect, pixel_buffer, pixel_byte_count, stride);
            Marshal.Copy(pixel_buffer, source_pixels, 0, pixel_byte_count);
        }
        finally
        {
            Marshal.FreeHGlobal(pixel_buffer);
        }

        var dib_data = new byte[checked(BitmapInfoHeaderSize + pixel_byte_count)];
        var header = dib_data.AsSpan(0, BitmapInfoHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], BitmapInfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], pixel_size.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], pixel_size.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..14], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..16], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..24], (uint)pixel_byte_count);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..28], Dpi96PixelsPerMeter);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..32], Dpi96PixelsPerMeter);

        var is_rgba = pixel_format == PixelFormats.Rgba8888;
        var alpha_format = bitmap.AlphaFormat;
        for (var source_y = 0; source_y < pixel_size.Height; source_y++)
        {
            var target_y = pixel_size.Height - source_y - 1;
            var source_row_offset = source_y * stride;
            var target_row_offset = BitmapInfoHeaderSize + target_y * stride;

            for (var x = 0; x < pixel_size.Width; x++)
            {
                var source_offset = source_row_offset + x * 4;
                var target_offset = target_row_offset + x * 4;
                var red = source_pixels[source_offset + (is_rgba ? 0 : 2)];
                var green = source_pixels[source_offset + 1];
                var blue = source_pixels[source_offset + (is_rgba ? 2 : 0)];
                var alpha = source_pixels[source_offset + 3];

                if (alpha_format == AlphaFormat.Premul)
                {
                    red = CompositePremultipliedOverWhite(red, alpha);
                    green = CompositePremultipliedOverWhite(green, alpha);
                    blue = CompositePremultipliedOverWhite(blue, alpha);
                }
                else if (alpha_format == AlphaFormat.Unpremul)
                {
                    red = CompositeStraightOverWhite(red, alpha);
                    green = CompositeStraightOverWhite(green, alpha);
                    blue = CompositeStraightOverWhite(blue, alpha);
                }

                dib_data[target_offset] = blue;
                dib_data[target_offset + 1] = green;
                dib_data[target_offset + 2] = red;
                dib_data[target_offset + 3] = 255;
            }
        }

        return dib_data;
    }

    private static byte CompositePremultipliedOverWhite(byte color, byte alpha)
    {
        var composited_color = color + 255 - alpha;
        return (byte)Math.Min(composited_color, 255);
    }

    private static byte CompositeStraightOverWhite(byte color, byte alpha)
    {
        var composited_color = color * alpha + 255 * (255 - alpha);
        return (byte)((composited_color + 127) / 255);
    }

    private static nint AllocateClipboardMemory(byte[] data)
    {
        var memory_handle = GlobalAlloc(GmemMoveable | GmemZeroInit, (nuint)data.Length);
        if (memory_handle == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot allocate Windows clipboard memory.");

        var memory_pointer = GlobalLock(memory_handle);
        if (memory_pointer == nint.Zero)
        {
            var error_code = Marshal.GetLastWin32Error();
            GlobalFree(memory_handle);
            throw new Win32Exception(error_code, "Cannot lock Windows clipboard memory.");
        }

        try
        {
            Marshal.Copy(data, 0, memory_pointer, data.Length);
        }
        finally
        {
            GlobalUnlock(memory_handle);
        }

        return memory_handle;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint new_owner_handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory_handle);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format_name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory_handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint memory_handle);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory_handle);
}
