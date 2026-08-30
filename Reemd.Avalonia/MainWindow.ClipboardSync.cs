using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Reemd.Services;

namespace Reemd;

public partial class MainWindow
{
    private const int MaxClipboardBundleBytes = 10 * 1024 * 1024;
    private const int MaxClipboardItems = 16;
    private const int MaxClipboardRepresentations = 32;

    private async Task<ClipboardBundle?> GetClipboardBundleAsync()
    {
        await _clipboardAccessLock.WaitAsync();
        try
        {
            var clipboard_operation = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null) return null;

                using var clipboard_data = await clipboard.TryGetDataAsync();
                if (clipboard_data == null) return null;

                return await CaptureClipboardBundleAsync(clipboard_data);
            });
            var clipboard_bundle = await clipboard_operation;
            AcknowledgeClipboardRead();
            return clipboard_bundle;
        }
        finally
        {
            _clipboardAccessLock.Release();
        }
    }

    private static async Task<ClipboardBundle?> CaptureClipboardBundleAsync(IAsyncDataTransfer clipboard_data)
    {
        var bundle_items = new List<ClipboardBundleItem>();
        var representation_count = 0;
        var bundle_size = 0;

        foreach (var source_item in clipboard_data.Items)
        {
            if (bundle_items.Count >= MaxClipboardItems) break;

            var representations = new List<ClipboardRepresentation>();
            var representation_keys = new HashSet<string>(StringComparer.Ordinal);
            var prioritized_formats = source_item.Formats.OrderBy(GetClipboardFormatPriority);
            var source_formats = prioritized_formats.ToArray();

            foreach (var format in source_formats)
            {
                if (representation_count >= MaxClipboardRepresentations ||
                    bundle_size >= MaxClipboardBundleBytes)
                    break;

                var representation = await TryCaptureRepresentationAsync(source_item, format);

                if (representation == null) continue;

                var representation_key = $"{representation.FormatKind}:{representation.ValueType}:{representation.Identifier}";
                if (!representation_keys.Add(representation_key)) continue;
                if (representation.Data.Length > MaxClipboardBundleBytes) continue;
                if (bundle_size + representation.Data.Length > MaxClipboardBundleBytes) continue;

                representations.Add(representation);
                representation_count++;
                bundle_size += representation.Data.Length;
            }

            if (representations.Count > 0)
                bundle_items.Add(new ClipboardBundleItem(representations.ToArray()));
        }

        if (bundle_items.Count == 0) return null;
        return new ClipboardBundle(ClipboardBundle.GetCurrentPlatform(), bundle_items.ToArray());
    }

    private static int GetClipboardFormatPriority(DataFormat format)
    {
        if (format == DataFormat.Text) return 0;
        if (format == DataFormat.Bitmap) return 1;
        if (format.Kind == DataFormatKind.Application) return 2;
        if (format.Kind == DataFormatKind.Platform) return 3;
        return 4;
    }

    private static async Task<ClipboardRepresentation?> TryCaptureRepresentationAsync(
        IAsyncDataTransferItem source_item,
        DataFormat format)
    {
        try
        {
            if (format == DataFormat.Text)
            {
                var text = await source_item.TryGetTextAsync();
                if (text == null) return null;

                var text_data = Encoding.UTF8.GetBytes(text);
                return new ClipboardRepresentation("text/plain", "text", "universal", text_data);
            }

            if (format == DataFormat.Bitmap)
            {
                var bitmap = await source_item.TryGetBitmapAsync();
                if (bitmap == null) return null;

                using var bitmap_stream = new MemoryStream();
#pragma warning disable CS0618
                bitmap.Save(bitmap_stream);
#pragma warning restore CS0618
                var bitmap_data = bitmap_stream.ToArray();
                return new ClipboardRepresentation("image/png", "bitmap", "universal", bitmap_data);
            }

            if (format.Kind is not (DataFormatKind.Application or DataFormatKind.Platform))
                return null;

            var raw_value = await source_item.TryGetRawAsync(format);
            var format_kind = format.Kind == DataFormatKind.Application
                ? "application"
                : "platform";
            if (raw_value is byte[] raw_bytes)
                return new ClipboardRepresentation(format.Identifier, "bytes", format_kind, raw_bytes);
            if (raw_value is string raw_string)
            {
                var string_data = Encoding.UTF8.GetBytes(raw_string);
                return new ClipboardRepresentation(format.Identifier, "string", format_kind, string_data);
            }
        }
        catch
        {
        }

        return null;
    }

    private Task<bool> HasClipboardChangedAsync()
    {
        if (_mac_clipboard_change_monitor != null)
            return Task.FromResult(_mac_clipboard_change_monitor.HasChanged());
        if (_windows_clipboard_change_monitor != null)
            return Task.FromResult(_windows_clipboard_change_monitor.HasChanged());

        return Task.FromResult(true);
    }

    private Task PublishClipboardAsync(string clipboard_text)
    {
        AcknowledgeCurrentClipboard();
        return Task.Run(() => _clipboardSyncService.PublishClipboardTextAsync(clipboard_text));
    }

    private async Task SetClipboardBundleAsync(ClipboardBundle clipboard_bundle)
    {
        await _clipboardAccessLock.WaitAsync();
        try
        {
            var clipboard_operation = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null)
                    throw new InvalidOperationException("Clipboard is unavailable.");

                var data_transfer = CreateDataTransfer(clipboard_bundle);
                await clipboard.SetDataAsync(data_transfer);
                await clipboard.FlushAsync();
            });
            await clipboard_operation;
            AcknowledgeCurrentClipboard();
        }
        finally
        {
            _clipboardAccessLock.Release();
        }
    }

    private static DataTransfer CreateDataTransfer(ClipboardBundle clipboard_bundle)
    {
        var data_transfer = new DataTransfer();
        var added_representation_count = 0;

        foreach (var bundle_item in clipboard_bundle.Items)
        {
            var data_transfer_item = new DataTransferItem();
            var item_representation_count = 0;

            foreach (var representation in bundle_item.Representations)
            {
                if (TrySetRepresentation(
                    data_transfer_item,
                    representation,
                    clipboard_bundle.SourcePlatform))
                {
                    item_representation_count++;
                    added_representation_count++;
                }
            }

            if (item_representation_count > 0)
                data_transfer.Add(data_transfer_item);
        }

        if (added_representation_count == 0)
            throw new InvalidDataException("Clipboard bundle contains no compatible formats.");

        return data_transfer;
    }

    private static bool TrySetRepresentation(
        DataTransferItem data_transfer_item,
        ClipboardRepresentation representation,
        string source_platform)
    {
        try
        {
            if (representation.FormatKind == "universal" && representation.ValueType == "text")
            {
                var text = Encoding.UTF8.GetString(representation.Data);
                data_transfer_item.SetText(text);
                return true;
            }

            if (representation.FormatKind == "universal" && representation.ValueType == "bitmap")
            {
                var bitmap_stream = new MemoryStream(representation.Data, writable: false);
                var bitmap = new Bitmap(bitmap_stream);
                bitmap_stream.Dispose();
                data_transfer_item.SetBitmap(bitmap);
                return true;
            }

            var target_identifier = GetTargetFormatIdentifier(
                representation.FormatKind,
                representation.Identifier,
                source_platform);
            if (target_identifier == null) return false;

            if (representation.ValueType == "bytes")
            {
                var bytes_format = representation.FormatKind == "application"
                    ? DataFormat.CreateBytesApplicationFormat(target_identifier)
                    : DataFormat.CreateBytesPlatformFormat(target_identifier);
                data_transfer_item.Set(bytes_format, representation.Data);
                return true;
            }

            if (representation.ValueType == "string")
            {
                var string_value = Encoding.UTF8.GetString(representation.Data);
                var string_format = representation.FormatKind == "application"
                    ? DataFormat.CreateStringApplicationFormat(target_identifier)
                    : DataFormat.CreateStringPlatformFormat(target_identifier);
                data_transfer_item.Set(string_format, string_value);
                return true;
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidDataException)
        {
        }

        return false;
    }

    private static string? GetTargetFormatIdentifier(
        string format_kind,
        string identifier,
        string source_platform)
    {
        if (format_kind == "application") return identifier;

        var target_platform = ClipboardBundle.GetCurrentPlatform();
        if (source_platform == target_platform) return identifier;

        var portable_format = GetPortableImageFormat(identifier);
        return portable_format switch
        {
            "png" => target_platform switch
            {
                "windows" => "PNG",
                "macos" => "public.png",
                _ => "image/png"
            },
            "jpeg" => target_platform switch
            {
                "windows" => "JFIF",
                "macos" => "public.jpeg",
                _ => "image/jpeg"
            },
            "tiff" => target_platform switch
            {
                "windows" => "TIFF",
                "macos" => "public.tiff",
                _ => "image/tiff"
            },
            "gif" => target_platform switch
            {
                "windows" => "GIF",
                "macos" => "com.compuserve.gif",
                _ => "image/gif"
            },
            _ => null
        };
    }

    private static string? GetPortableImageFormat(string identifier)
    {
        if (identifier.Equals("PNG", StringComparison.OrdinalIgnoreCase) ||
            identifier.Equals("public.png", StringComparison.OrdinalIgnoreCase) ||
            identifier.Equals("image/png", StringComparison.OrdinalIgnoreCase))
            return "png";
        if (identifier.Equals("JFIF", StringComparison.OrdinalIgnoreCase) ||
            identifier.Equals("public.jpeg", StringComparison.OrdinalIgnoreCase) ||
            identifier.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase))
            return "jpeg";
        if (identifier.Equals("TIFF", StringComparison.OrdinalIgnoreCase) ||
            identifier.Equals("public.tiff", StringComparison.OrdinalIgnoreCase) ||
            identifier.Equals("image/tiff", StringComparison.OrdinalIgnoreCase))
            return "tiff";
        if (identifier.Equals("GIF", StringComparison.OrdinalIgnoreCase) ||
            identifier.Equals("com.compuserve.gif", StringComparison.OrdinalIgnoreCase) ||
            identifier.Equals("image/gif", StringComparison.OrdinalIgnoreCase))
            return "gif";

        return null;
    }

    private void AcknowledgeCurrentClipboard()
    {
        _mac_clipboard_change_monitor?.AcknowledgeCurrent();
        _windows_clipboard_change_monitor?.AcknowledgeCurrent();
    }

    private void AcknowledgeClipboardRead()
    {
        _mac_clipboard_change_monitor?.AcknowledgeRead();
        _windows_clipboard_change_monitor?.AcknowledgeRead();
    }
}
