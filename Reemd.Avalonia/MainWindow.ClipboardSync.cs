using System.Text;
using Avalonia;
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
                if (clipboard == null)
                {
                    _clipboard_sync_logger.Log("Clipboard capture failed: OS clipboard is unavailable");
                    return null;
                }

                using var clipboard_data = await clipboard.TryGetDataAsync();
                if (clipboard_data == null)
                {
                    _clipboard_sync_logger.Log("Clipboard capture completed: OS returned no data");
                    return null;
                }

                return await CaptureClipboardBundleAsync(clipboard_data);
            });
            var clipboard_bundle = await clipboard_operation;
            AcknowledgeClipboardRead();
            return clipboard_bundle;
        }
        catch (Exception exception)
        {
            _clipboard_sync_logger.Log($"Clipboard capture error: error={exception.GetType().Name}");
            throw;
        }
        finally
        {
            _clipboardAccessLock.Release();
        }
    }

    private async Task<ClipboardBundle?> CaptureClipboardBundleAsync(IAsyncDataTransfer clipboard_data)
    {
        var bundle_items = new List<ClipboardBundleItem>();
        var representation_count = 0;
        var bundle_size = 0;
        var source_item_number = 0;

        foreach (var source_item in clipboard_data.Items)
        {
            if (bundle_items.Count >= MaxClipboardItems) break;

            source_item_number++;
            var item_number = source_item_number;
            var representations = new List<ClipboardRepresentation>();
            var representation_keys = new HashSet<string>(StringComparer.Ordinal);
            var prioritized_formats = source_item.Formats.OrderBy(GetClipboardFormatPriority);
            var source_formats = prioritized_formats.ToArray();
            var advertised_formats = source_formats.Select(DescribeSourceFormat);
            var advertised_format_summary = string.Join("; ", advertised_formats);
            _clipboard_sync_logger.Log(
                $"Clipboard item recognized: item={item_number}, advertised_formats=[{advertised_format_summary}]");

            foreach (var format in source_formats)
            {
                if (representation_count >= MaxClipboardRepresentations ||
                    bundle_size >= MaxClipboardBundleBytes)
                    break;

                var is_image_format = format == DataFormat.Bitmap ||
                    GetPortableImageFormat(format.Identifier) != null;
                if (is_image_format)
                {
                    _clipboard_sync_logger.Log(
                        $"Clipboard image format recognized: item={item_number}, " +
                        $"identifier={format.Identifier}, kind={format.Kind}");
                }

                var representation = await TryCaptureRepresentationAsync(source_item, format, item_number);

                if (representation == null) continue;

                var representation_key = $"{representation.FormatKind}:{representation.ValueType}:{representation.Identifier}";
                if (!representation_keys.Add(representation_key))
                {
                    _clipboard_sync_logger.Log(
                        $"Clipboard format skipped: item={item_number}, identifier={representation.Identifier}, reason=duplicate");
                    continue;
                }
                if (representation.Data.Length > MaxClipboardBundleBytes)
                {
                    _clipboard_sync_logger.Log(
                        $"Clipboard format skipped: item={item_number}, identifier={representation.Identifier}, " +
                        $"bytes={representation.Data.Length}, reason=representation limit");
                    continue;
                }
                if (bundle_size + representation.Data.Length > MaxClipboardBundleBytes)
                {
                    _clipboard_sync_logger.Log(
                        $"Clipboard format skipped: item={item_number}, identifier={representation.Identifier}, " +
                        $"bytes={representation.Data.Length}, reason=bundle limit");
                    continue;
                }

                representations.Add(representation);
                representation_count++;
                bundle_size += representation.Data.Length;
                var image_label = representation.ValueType == "bitmap" ||
                    GetPortableImageFormat(representation.Identifier) != null
                    ? ", image=yes"
                    : string.Empty;
                _clipboard_sync_logger.Log(
                    $"Clipboard format captured: item={item_number}, identifier={representation.Identifier}, " +
                    $"kind={representation.FormatKind}, type={representation.ValueType}, " +
                    $"bytes={representation.Data.Length}{image_label}");
            }

            if (representations.Count > 0)
                bundle_items.Add(new ClipboardBundleItem(representations.ToArray()));
        }

        if (bundle_items.Count == 0) return null;

        var clipboard_bundle = new ClipboardBundle(ClipboardBundle.GetCurrentPlatform(), bundle_items.ToArray());
        _clipboard_sync_logger.Log(
            $"Clipboard capture completed: source={clipboard_bundle.SourcePlatform}, bytes={bundle_size}, " +
            $"formats=[{clipboard_bundle.DescribeFormats()}]");
        return clipboard_bundle;
    }

    private static int GetClipboardFormatPriority(DataFormat format)
    {
        if (format == DataFormat.Text) return 0;
        if (format == DataFormat.Bitmap) return 1;
        if (format.Kind == DataFormatKind.Application) return 2;
        if (format.Kind == DataFormatKind.Platform) return 3;
        return 4;
    }

    private async Task<ClipboardRepresentation?> TryCaptureRepresentationAsync(
        IAsyncDataTransferItem source_item,
        DataFormat format,
        int item_number)
    {
        try
        {
            if (format == DataFormat.Text)
            {
                var text = await source_item.TryGetTextAsync();
                if (text == null)
                {
                    _clipboard_sync_logger.Log(
                        $"Clipboard format unavailable: item={item_number}, identifier={format.Identifier}, type=text");
                    return null;
                }

                var text_data = Encoding.UTF8.GetBytes(text);
                return new ClipboardRepresentation("text/plain", "text", "universal", text_data);
            }

            if (format == DataFormat.Bitmap)
            {
                var bitmap = await source_item.TryGetBitmapAsync();
                if (bitmap == null)
                {
                    _clipboard_sync_logger.Log(
                        $"Clipboard image unavailable: item={item_number}, identifier={format.Identifier}, type=bitmap");
                    return null;
                }

                _clipboard_sync_logger.Log(
                    $"Clipboard image recognized: item={item_number}, identifier={format.Identifier}, source=bitmap");

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

            var raw_type = raw_value?.GetType().FullName ?? "null";
            _clipboard_sync_logger.Log(
                $"Clipboard format unsupported: item={item_number}, identifier={format.Identifier}, " +
                $"kind={format_kind}, raw_type={raw_type}");
        }
        catch (Exception exception)
        {
            _clipboard_sync_logger.Log(
                $"Clipboard format capture error: item={item_number}, identifier={format.Identifier}, " +
                $"kind={format.Kind}, error={exception.GetType().Name}");
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

    private static string DescribeSourceFormat(DataFormat format)
    {
        var identifier = format.Identifier.Replace('\r', ' ');
        identifier = identifier.Replace('\n', ' ');
        identifier = identifier.Replace('\t', ' ');
        var image_label = format == DataFormat.Bitmap || GetPortableImageFormat(identifier) != null
            ? ", image=yes"
            : string.Empty;
        return $"identifier={identifier}, kind={format.Kind}{image_label}";
    }

    private Task PublishClipboardAsync(string clipboard_text)
    {
        AcknowledgeCurrentClipboard();
        return Task.Run(() => _clipboardSyncService.PublishClipboardTextAsync(clipboard_text));
    }

    private async Task SetClipboardBundleAsync(ClipboardBundle clipboard_bundle)
    {
        _clipboard_sync_logger.Log(
            $"Clipboard OS write started: source={clipboard_bundle.SourcePlatform}, " +
            $"bytes={clipboard_bundle.GetByteCount()}, formats=[{clipboard_bundle.DescribeFormats()}]");
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
            _clipboard_sync_logger.Log("Clipboard OS write completed and change monitor acknowledged");
        }
        catch (Exception exception)
        {
            _clipboard_sync_logger.Log($"Clipboard OS write error: error={exception.GetType().Name}");
            throw;
        }
        finally
        {
            _clipboardAccessLock.Release();
        }
    }

    private DataTransfer CreateDataTransfer(ClipboardBundle clipboard_bundle)
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

    private bool TrySetRepresentation(
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
                _clipboard_sync_logger.Log(
                    $"Clipboard format applied: identifier={representation.Identifier}, target=text, " +
                    $"type={representation.ValueType}, bytes={representation.Data.Length}");
                return true;
            }

            if (representation.FormatKind == "universal" && representation.ValueType == "bitmap")
            {
                var bitmap_stream = new MemoryStream(representation.Data, writable: false);
                var source_bitmap = new Bitmap(bitmap_stream);
                bitmap_stream.Dispose();
                var clipboard_bitmap = PrepareClipboardBitmap(source_bitmap);
                if (!ReferenceEquals(source_bitmap, clipboard_bitmap))
                    source_bitmap.Dispose();

                data_transfer_item.SetBitmap(clipboard_bitmap);
                var pixel_size = clipboard_bitmap.PixelSize;
                var pixel_format = clipboard_bitmap.Format?.ToString() ?? "unknown";
                var alpha_format = clipboard_bitmap.AlphaFormat?.ToString() ?? "unknown";
                _clipboard_sync_logger.Log(
                    $"Clipboard image applied: identifier={representation.Identifier}, target=bitmap, " +
                    $"bytes={representation.Data.Length}, pixels={pixel_size.Width}x{pixel_size.Height}, " +
                    $"pixel_format={pixel_format}, alpha_format={alpha_format}");
                return true;
            }

            var target_identifier = GetTargetFormatIdentifier(
                representation.FormatKind,
                representation.Identifier,
                source_platform);
            if (target_identifier == null)
            {
                _clipboard_sync_logger.Log(
                    $"Clipboard format not applied: identifier={representation.Identifier}, " +
                    $"kind={representation.FormatKind}, type={representation.ValueType}, " +
                    $"source={source_platform}, target={ClipboardBundle.GetCurrentPlatform()}, reason=incompatible format");
                return false;
            }

            if (representation.ValueType == "bytes")
            {
                var bytes_format = representation.FormatKind == "application"
                    ? DataFormat.CreateBytesApplicationFormat(target_identifier)
                    : DataFormat.CreateBytesPlatformFormat(target_identifier);
                data_transfer_item.Set(bytes_format, representation.Data);
                _clipboard_sync_logger.Log(
                    $"Clipboard format applied: identifier={representation.Identifier}, target={target_identifier}, " +
                    $"type=bytes, bytes={representation.Data.Length}");
                return true;
            }

            if (representation.ValueType == "string")
            {
                var string_value = Encoding.UTF8.GetString(representation.Data);
                var string_format = representation.FormatKind == "application"
                    ? DataFormat.CreateStringApplicationFormat(target_identifier)
                    : DataFormat.CreateStringPlatformFormat(target_identifier);
                data_transfer_item.Set(string_format, string_value);
                _clipboard_sync_logger.Log(
                    $"Clipboard format applied: identifier={representation.Identifier}, target={target_identifier}, " +
                    $"type=string, bytes={representation.Data.Length}");
                return true;
            }
        }
        catch (ArgumentException exception)
        {
            _clipboard_sync_logger.Log(
                $"Clipboard format apply error: identifier={representation.Identifier}, error={exception.GetType().Name}");
        }
        catch (InvalidDataException exception)
        {
            _clipboard_sync_logger.Log(
                $"Clipboard format apply error: identifier={representation.Identifier}, error={exception.GetType().Name}");
        }
        catch (Exception exception)
        {
            _clipboard_sync_logger.Log(
                $"Clipboard format apply error: identifier={representation.Identifier}, error={exception.GetType().Name}");
            throw;
        }

        return false;
    }

    private Bitmap PrepareClipboardBitmap(Bitmap source_bitmap)
    {
        var source_pixel_size = source_bitmap.PixelSize;
        var source_pixel_format = source_bitmap.Format?.ToString() ?? "unknown";
        var source_alpha_format = source_bitmap.AlphaFormat?.ToString() ?? "unknown";
        _clipboard_sync_logger.Log(
            $"Clipboard image decoded: pixels={source_pixel_size.Width}x{source_pixel_size.Height}, " +
            $"pixel_format={source_pixel_format}, alpha_format={source_alpha_format}, " +
            $"dpi={source_bitmap.Dpi.X}x{source_bitmap.Dpi.Y}");

        if (!OperatingSystem.IsWindows()) return source_bitmap;

        var clipboard_dpi = new Vector(96, 96);
        var normalized_bitmap = new RenderTargetBitmap(source_pixel_size, clipboard_dpi);
        using var drawing_context = normalized_bitmap.CreateDrawingContext();
        var source_size = source_bitmap.Size;
        var source_rect = new Rect(0, 0, source_size.Width, source_size.Height);
        var target_rect = new Rect(0, 0, source_pixel_size.Width, source_pixel_size.Height);
        drawing_context.DrawImage(source_bitmap, source_rect, target_rect);

        var normalized_pixel_format = normalized_bitmap.Format?.ToString() ?? "unknown";
        var normalized_alpha_format = normalized_bitmap.AlphaFormat?.ToString() ?? "unknown";
        _clipboard_sync_logger.Log(
            $"Clipboard image normalized for Windows DIB: pixels={source_pixel_size.Width}x{source_pixel_size.Height}, " +
            $"pixel_format={normalized_pixel_format}, alpha_format={normalized_alpha_format}, dpi=96x96");
        return normalized_bitmap;
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
