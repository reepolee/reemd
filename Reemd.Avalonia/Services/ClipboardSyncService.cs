using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Reemd.Services;

/// <summary>
/// Synchronizes text clipboard changes with other ReeMD instances on one LAN multicast channel.
/// </summary>
public sealed class ClipboardSyncService : IDisposable
{
    private const int PollIntervalMs = 300;
    private const int MaxPayloadBytes = 48 * 1024;

    private readonly Func<Task<string?>> _clipboardTextReader;
    private readonly Func<string, Task> _clipboardTextWriter;
    private readonly string _senderId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly object _lifecycleLock = new();
    private readonly ClipboardSyncLogger _logger = new();
    private string _channel;
    private string? _lastClipboardText;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cancellationTokenSource;

    public event Action<string>? StatusChanged;

    public string LogPath => _logger.LogPath;

    public ClipboardSyncService(
        Func<Task<string?>> clipboardTextReader,
        Func<string, Task> clipboardTextWriter,
        string channel)
    {
        _clipboardTextReader = clipboardTextReader;
        _clipboardTextWriter = clipboardTextWriter;
        _channel = channel;
    }

    public static bool IsValidChannel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) || channel.Length > 64) return false;

        return channel.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_cancellationTokenSource != null) return;

            UdpClient? udpClient = null;
            try
            {
                var endpoint = GetChannelEndpoint(_channel);
                udpClient = new UdpClient(AddressFamily.InterNetwork);
                udpClient.ExclusiveAddressUse = false;
                udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, endpoint.Port));
                udpClient.JoinMulticastGroup(endpoint.Address);
                udpClient.MulticastLoopback = true;

                var cancellationTokenSource = new CancellationTokenSource();
                _udpClient = udpClient;
                _cancellationTokenSource = cancellationTokenSource;

                _ = ReceiveLoopAsync(udpClient, cancellationTokenSource.Token);
                _ = PollClipboardLoopAsync(cancellationTokenSource.Token);
                Report($"Clipboard listening: {_channel} ({endpoint.Address}:{endpoint.Port})");
            }
            catch (SocketException exception)
            {
                udpClient?.Dispose();
                Report($"Clipboard listener error: {exception.SocketErrorCode}");
            }
            catch (Exception exception)
            {
                udpClient?.Dispose();
                Report($"Clipboard listener error: {exception.GetType().Name}");
            }
        }
    }

    public void UpdateChannel(string channel)
    {
        if (!IsValidChannel(channel)) throw new ArgumentException("Invalid clipboard channel.", nameof(channel));

        Stop();
        _channel = channel;
        _lastClipboardText = null;
        _logger.Log($"Clipboard channel changed: {channel}");
        Start();
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            var cancellationTokenSource = _cancellationTokenSource;
            var udpClient = _udpClient;
            _cancellationTokenSource = null;
            _udpClient = null;

            cancellationTokenSource?.Cancel();
            udpClient?.Dispose();
            cancellationTokenSource?.Dispose();
            _logger.Log("Clipboard listener stopped");
        }
    }

    private async Task PollClipboardLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollIntervalMs));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendChangedClipboardAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendChangedClipboardAsync(CancellationToken cancellationToken)
    {
        if (!await _pollLock.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;

        try
        {
            var clipboardText = await _clipboardTextReader().ConfigureAwait(false);
            if (clipboardText == null || clipboardText == _lastClipboardText) return;

            _lastClipboardText = clipboardText;
            var envelope = new ClipboardEnvelope(1, _channel, _senderId, clipboardText);
            var payload = JsonSerializer.SerializeToUtf8Bytes(envelope);
            if (payload.Length > MaxPayloadBytes)
            {
                Report($"Clipboard update skipped: {payload.Length} bytes exceeds {MaxPayloadBytes} byte limit");
                return;
            }

            var udpClient = _udpClient;
            if (udpClient == null) return;

            var endpoint = GetChannelEndpoint(_channel);
            await udpClient.SendAsync(payload, endpoint, cancellationToken).ConfigureAwait(false);
            Report($"Clipboard sent: {payload.Length} bytes on {_channel}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException exception)
        {
            Report($"Clipboard send error: {exception.SocketErrorCode}");
        }
        catch (Exception exception)
        {
            Report($"Clipboard send error: {exception.GetType().Name}");
        }
        finally
        {
            _pollLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udpClient, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                ClipboardEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<ClipboardEnvelope>(result.Buffer);
                }
                catch (JsonException)
                {
                    _logger.Log($"Clipboard ignored malformed packet from {result.RemoteEndPoint}");
                    continue;
                }

                if (envelope is not { Version: 1 } || envelope.Channel != _channel || envelope.SenderId == _senderId)
                    continue;

                var textSize = Encoding.UTF8.GetByteCount(envelope.Text);
                if (textSize > MaxPayloadBytes) continue;

                _lastClipboardText = envelope.Text;
                try
                {
                    await _clipboardTextWriter(envelope.Text).ConfigureAwait(false);
                    Report($"Clipboard received: {textSize} bytes from {result.RemoteEndPoint.Address}");
                }
                catch (Exception exception)
                {
                    Report($"Clipboard write error: {exception.GetType().Name}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException exception)
        {
            Report($"Clipboard receive error: {exception.SocketErrorCode}");
        }
    }

    private static IPEndPoint GetChannelEndpoint(string channel)
    {
        var channelBytes = Encoding.UTF8.GetBytes(channel);
        var hash = SHA256.HashData(channelBytes);
        var multicastAddress = new IPAddress([239, 192, hash[0], hash[1]]);
        var port = 45000 + ((hash[2] << 8 | hash[3]) % 1000);
        return new IPEndPoint(multicastAddress, port);
    }

    public void Dispose()
    {
        Stop();
        _pollLock.Dispose();
    }

    private void Report(string message)
    {
        _logger.Log(message);
        StatusChanged?.Invoke(message);
    }

    private sealed record ClipboardEnvelope(int Version, string Channel, string SenderId, string Text);
}
