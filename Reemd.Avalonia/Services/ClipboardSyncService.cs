using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Reemd.Services;

/// <summary>
/// Synchronizes text clipboard changes with configured LAN peers over TCP.
/// </summary>
public sealed class ClipboardSyncService : IDisposable
{
    private const int PollIntervalMs = 750;
    private const int MaxPayloadBytes = 48 * 1024;
    private const int ConnectTimeoutMs = 1500;

    private readonly Func<Task<string?>> _clipboard_text_reader;
    private readonly Func<string, Task> _clipboard_text_writer;
    private readonly Func<Task<bool>> _clipboard_change_checker;
    private readonly string _sender_id = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _poll_lock = new(1, 1);
    private readonly object _lifecycle_lock = new();
    private readonly ClipboardSyncLogger _logger = new();
    private string _channel;
    private string[] _peer_addresses;
    private string? _last_clipboard_text;
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation_token_source;
    private long _poll_suspended_until;

    public event Action<string>? StatusChanged;

    public string LogPath => _logger.LogPath;

    public ClipboardSyncService(
        Func<Task<string?>> clipboard_text_reader,
        Func<string, Task> clipboard_text_writer,
        Func<Task<bool>> clipboard_change_checker,
        string channel,
        IEnumerable<string> peer_addresses)
    {
        _clipboard_text_reader = clipboard_text_reader;
        _clipboard_text_writer = clipboard_text_writer;
        _clipboard_change_checker = clipboard_change_checker;
        _channel = channel;
        _peer_addresses = peer_addresses.ToArray();
    }

    public static bool IsValidChannel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) || channel.Length > 64) return false;

        return channel.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');
    }

    public static bool IsValidPeerAddress(string address)
    {
        return IPAddress.TryParse(address, out var ip_address) &&
            ip_address.AddressFamily == AddressFamily.InterNetwork;
    }

    public void Start()
    {
        lock (_lifecycle_lock)
        {
            if (_cancellation_token_source != null) return;

            var port = GetChannelPort(_channel);
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();

                var cancellation_token_source = new CancellationTokenSource();
                _listener = listener;
                _cancellation_token_source = cancellation_token_source;

                _ = AcceptLoopAsync(listener, cancellation_token_source.Token);
                _ = PollClipboardLoopAsync(cancellation_token_source.Token);
                Report($"Clipboard TCP listening: {_channel} on port {port} ({_peer_addresses.Length} peer(s))");
            }
            catch (SocketException exception)
            {
                Report($"Clipboard TCP listener error: {exception.SocketErrorCode}");
            }
        }
    }

    public void UpdateChannel(string channel)
    {
        if (!IsValidChannel(channel)) throw new ArgumentException("Invalid clipboard channel.", nameof(channel));

        Stop();
        _channel = channel;
        _last_clipboard_text = null;
        _logger.Log($"Clipboard channel changed: {channel}");
        Start();
    }

    public void UpdatePeers(IEnumerable<string> peer_addresses)
    {
        var peer_address_array = peer_addresses.ToArray();
        if (peer_address_array.Any(address => !IsValidPeerAddress(address)))
            throw new ArgumentException("Invalid clipboard peer address.", nameof(peer_addresses));

        _peer_addresses = peer_address_array;
        Report($"Clipboard TCP peers updated: {_peer_addresses.Length} peer(s)");
    }

    public void SuspendPolling(TimeSpan duration)
    {
        var suspended_until = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        var current_suspended_until = Volatile.Read(ref _poll_suspended_until);
        while (suspended_until > current_suspended_until)
        {
            var previous_suspended_until = Interlocked.CompareExchange(
                ref _poll_suspended_until,
                suspended_until,
                current_suspended_until);
            if (previous_suspended_until == current_suspended_until) return;

            current_suspended_until = previous_suspended_until;
        }
    }

    public Task PublishCurrentClipboardAsync()
    {
        return SendChangedClipboardAsync(CancellationToken.None, true);
    }

    public void Stop()
    {
        lock (_lifecycle_lock)
        {
            var cancellation_token_source = _cancellation_token_source;
            var listener = _listener;
            _cancellation_token_source = null;
            _listener = null;

            cancellation_token_source?.Cancel();
            listener?.Stop();
            cancellation_token_source?.Dispose();
            _logger.Log("Clipboard TCP listener stopped");
        }
    }

    private async Task PollClipboardLoopAsync(CancellationToken cancellation_token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollIntervalMs));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellation_token).ConfigureAwait(false))
            {
                if (Environment.TickCount64 < Volatile.Read(ref _poll_suspended_until)) continue;

                await SendChangedClipboardAsync(cancellation_token, false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendChangedClipboardAsync(CancellationToken cancellation_token, bool force_publish)
    {
        if (!await _poll_lock.WaitAsync(0, cancellation_token).ConfigureAwait(false)) return;

        try
        {
            if (!force_publish)
            {
                var clipboard_changed = await _clipboard_change_checker().ConfigureAwait(false);
                if (!clipboard_changed) return;
            }

            var clipboard_text = await _clipboard_text_reader().ConfigureAwait(false);
            if (clipboard_text == null || clipboard_text == _last_clipboard_text) return;

            _last_clipboard_text = clipboard_text;
            var envelope = new ClipboardEnvelope(1, _channel, _sender_id, clipboard_text);
            var payload = JsonSerializer.SerializeToUtf8Bytes(envelope);
            if (payload.Length > MaxPayloadBytes)
            {
                Report($"Clipboard update skipped: {payload.Length} bytes exceeds {MaxPayloadBytes} byte limit");
                return;
            }

            var peer_addresses = _peer_addresses;
            if (peer_addresses.Length == 0)
            {
                Report("Clipboard change detected, but no TCP peers are configured");
                return;
            }

            var port = GetChannelPort(_channel);
            var sent_count = 0;
            foreach (var peer_address in peer_addresses)
            {
                if (await SendEnvelopeAsync(peer_address, port, payload, cancellation_token).ConfigureAwait(false))
                    sent_count++;
            }

            Report($"Clipboard sent: {payload.Length} bytes to {sent_count}/{peer_addresses.Length} TCP peer(s)");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Report($"Clipboard send error: {exception.GetType().Name}");
        }
        finally
        {
            _poll_lock.Release();
        }
    }

    private async Task<bool> SendEnvelopeAsync(string peer_address, int port, byte[] payload, CancellationToken cancellation_token)
    {
        try
        {
            using var tcp_client = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true
            };
            using var connect_cancellation_token_source = CancellationTokenSource.CreateLinkedTokenSource(cancellation_token);
            connect_cancellation_token_source.CancelAfter(ConnectTimeoutMs);
            await tcp_client.ConnectAsync(peer_address, port, connect_cancellation_token_source.Token).ConfigureAwait(false);

            await using var stream = tcp_client.GetStream();
            await WriteEnvelopeAsync(stream, payload, cancellation_token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellation_token.IsCancellationRequested)
        {
            _logger.Log($"Clipboard TCP connection timed out: {peer_address}:{port}");
            return false;
        }
        catch (SocketException exception)
        {
            _logger.Log($"Clipboard TCP connection failed: {peer_address}:{port} ({exception.SocketErrorCode})");
            return false;
        }
        catch (Exception exception)
        {
            _logger.Log($"Clipboard TCP connection failed: {peer_address}:{port} ({exception.GetType().Name})");
            return false;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellation_token)
    {
        try
        {
            while (!cancellation_token.IsCancellationRequested)
            {
                var tcp_client = await listener.AcceptTcpClientAsync(cancellation_token).ConfigureAwait(false);
                _ = ReceiveEnvelopeAsync(tcp_client, cancellation_token);
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
            Report($"Clipboard TCP listener error: {exception.SocketErrorCode}");
        }
    }

    private async Task ReceiveEnvelopeAsync(TcpClient tcp_client, CancellationToken cancellation_token)
    {
        using (tcp_client)
        {
            try
            {
                await using var stream = tcp_client.GetStream();
                var payload = await ReadEnvelopeAsync(stream, cancellation_token).ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize<ClipboardEnvelope>(payload);
                if (envelope is not { Version: 1 } || envelope.Channel != _channel || envelope.SenderId == _sender_id)
                    return;

                var text_size = Encoding.UTF8.GetByteCount(envelope.Text);
                if (text_size > MaxPayloadBytes) return;

                _last_clipboard_text = envelope.Text;
                await _clipboard_text_writer(envelope.Text).ConfigureAwait(false);
                var remote_endpoint = tcp_client.Client.RemoteEndPoint as IPEndPoint;
                Report($"Clipboard received: {text_size} bytes from {remote_endpoint?.Address}");
            }
            catch (OperationCanceledException)
            {
            }
            catch (JsonException)
            {
                _logger.Log("Clipboard TCP ignored malformed payload");
            }
            catch (EndOfStreamException)
            {
            }
            catch (Exception exception)
            {
                _logger.Log($"Clipboard TCP receive failed: {exception.GetType().Name}");
            }
        }
    }

    private static async Task WriteEnvelopeAsync(NetworkStream stream, byte[] payload, CancellationToken cancellation_token)
    {
        var length_buffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length_buffer, payload.Length);
        await stream.WriteAsync(length_buffer, cancellation_token).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellation_token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadEnvelopeAsync(NetworkStream stream, CancellationToken cancellation_token)
    {
        var length_buffer = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(length_buffer, cancellation_token).ConfigureAwait(false);
        var payload_length = BinaryPrimitives.ReadInt32BigEndian(length_buffer);
        if (payload_length <= 0 || payload_length > MaxPayloadBytes)
            throw new InvalidDataException("Invalid clipboard TCP payload length.");

        var payload = new byte[payload_length];
        await stream.ReadExactlyAsync(payload, cancellation_token).ConfigureAwait(false);
        return payload;
    }

    private static int GetChannelPort(string channel)
    {
        var channel_bytes = Encoding.UTF8.GetBytes(channel);
        var hash = SHA256.HashData(channel_bytes);
        return 45000 + ((hash[2] << 8 | hash[3]) % 1000);
    }

    public void Dispose()
    {
        Stop();
        _poll_lock.Dispose();
    }

    private void Report(string message)
    {
        _logger.Log(message);
        StatusChanged?.Invoke(message);
    }

    private sealed record ClipboardEnvelope(int Version, string Channel, string SenderId, string Text);
}
