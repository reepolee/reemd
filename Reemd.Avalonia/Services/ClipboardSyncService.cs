using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Reemd.Services;

/// <summary>
/// Synchronizes text clipboard changes with LAN peers over persistent TCP connections.
/// </summary>
public sealed class ClipboardSyncService : IDisposable
{
    private const int ProtocolVersion = 2;
    private const int PollIntervalMs = 750;
    private const int ConnectionCheckIntervalMs = 2000;
    private const int HeartbeatIntervalSeconds = 10;
    private const int HeartbeatTimeoutMs = 30_000;
    private const int ConnectTimeoutMs = 3000;
    private const int HandshakeTimeoutMs = 3000;
    private const int MaxTextBytes = 48 * 1024;
    private const int MaxPayloadBytes = 64 * 1024;

    private readonly Func<Task<string?>> _clipboard_text_reader;
    private readonly Func<string, Task> _clipboard_text_writer;
    private readonly Func<Task<bool>> _clipboard_change_checker;
    private readonly string _sender_id = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _poll_lock = new(1, 1);
    private readonly object _lifecycle_lock = new();
    private readonly ClipboardSyncLogger _logger = new();
    private readonly ConcurrentDictionary<string, PeerConnection> _connections_by_sender = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MdnsClipboardPeer> _discovered_peers_by_sender = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _connecting_endpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _acked_message_by_sender = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _received_message_ids = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _received_message_order = new();
    private string _channel;
    private string[] _peer_addresses;
    private string? _last_clipboard_text;
    private ClipboardUpdate? _latest_local_update;
    private TcpListener? _listener;
    private MdnsClipboardDiscovery? _mdns_discovery;
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
                var mdns_discovery = new MdnsClipboardDiscovery(
                    _channel,
                    _sender_id,
                    port,
                    OnMdnsPeerDiscovered,
                    _logger.Log);

                _listener = listener;
                _mdns_discovery = mdns_discovery;
                _cancellation_token_source = cancellation_token_source;

                mdns_discovery.Start();
                _ = AcceptLoopAsync(listener, cancellation_token_source.Token);
                _ = PollClipboardLoopAsync(cancellation_token_source.Token);
                _ = ConnectionLoopAsync(cancellation_token_source.Token);
                _ = HeartbeatLoopAsync(cancellation_token_source.Token);
                Report($"Clipboard TCP listening: {_channel} on port {port} ({_peer_addresses.Length} configured peer(s))");
            }
            catch (Exception exception)
            {
                _listener?.Stop();
                _listener = null;
                _mdns_discovery?.Dispose();
                _mdns_discovery = null;
                _cancellation_token_source?.Dispose();
                _cancellation_token_source = null;
                Report($"Clipboard startup error: {exception.GetType().Name}");
            }
        }
    }

    public void UpdateChannel(string channel)
    {
        if (!IsValidChannel(channel)) throw new ArgumentException("Invalid clipboard channel.", nameof(channel));

        Stop();
        _channel = channel;
        _last_clipboard_text = null;
        _latest_local_update = null;
        _discovered_peers_by_sender.Clear();
        _acked_message_by_sender.Clear();
        _logger.Log($"Clipboard channel changed: {channel}");
        Start();
    }

    public void UpdatePeers(IEnumerable<string> peer_addresses)
    {
        var peer_address_array = peer_addresses.ToArray();
        if (peer_address_array.Any(address => !IsValidPeerAddress(address)))
            throw new ArgumentException("Invalid clipboard peer address.", nameof(peer_addresses));

        _peer_addresses = peer_address_array;
        Report($"Clipboard TCP configured peers updated: {_peer_addresses.Length}");
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

    public async Task PublishClipboardTextAsync(string clipboard_text)
    {
        _logger.Log($"Clipboard publish requested: {Encoding.UTF8.GetByteCount(clipboard_text)} bytes");
        await _poll_lock.WaitAsync().ConfigureAwait(false);

        try
        {
            await PublishLocalClipboardTextAsync(clipboard_text, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _poll_lock.Release();
        }
    }

    public void Stop()
    {
        lock (_lifecycle_lock)
        {
            var cancellation_token_source = _cancellation_token_source;
            var listener = _listener;
            var mdns_discovery = _mdns_discovery;
            _cancellation_token_source = null;
            _listener = null;
            _mdns_discovery = null;

            cancellation_token_source?.Cancel();
            listener?.Stop();
            mdns_discovery?.Dispose();

            var connections = _connections_by_sender.Values.ToArray();
            _connections_by_sender.Clear();
            foreach (var connection in connections)
            {
                connection.Close();
            }

            _connecting_endpoints.Clear();
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

            await PublishLocalClipboardTextAsync(clipboard_text, cancellation_token).ConfigureAwait(false);
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

    private async Task PublishLocalClipboardTextAsync(string clipboard_text, CancellationToken cancellation_token)
    {
        var text_size = Encoding.UTF8.GetByteCount(clipboard_text);
        if (text_size > MaxTextBytes)
        {
            Report($"Clipboard update skipped: {text_size} bytes exceeds {MaxTextBytes} byte limit");
            return;
        }

        ClipboardUpdate clipboard_update;
        if (_latest_local_update is { } latest_update && latest_update.Text == clipboard_text)
        {
            clipboard_update = latest_update;
        }
        else
        {
            clipboard_update = new ClipboardUpdate(Guid.NewGuid().ToString("N"), clipboard_text);
            _latest_local_update = clipboard_update;
        }

        _last_clipboard_text = clipboard_text;
        var connections = _connections_by_sender.Values.ToArray();
        if (connections.Length == 0)
        {
            Report($"Clipboard queued: {text_size} bytes, no connected peers");
            return;
        }

        var send_tasks = connections.Select(connection =>
            SendClipboardUpdateAsync(connection, clipboard_update, cancellation_token));
        var send_results = await Task.WhenAll(send_tasks).ConfigureAwait(false);
        var sent_count = send_results.Count(was_sent => was_sent);
        Report($"Clipboard sent: {text_size} bytes to {sent_count}/{connections.Length} connected peer(s)");
    }

    private async Task<bool> SendClipboardUpdateAsync(
        PeerConnection connection,
        ClipboardUpdate clipboard_update,
        CancellationToken cancellation_token)
    {
        var remote_sender_id = connection.RemoteSenderId;
        if (remote_sender_id == null) return false;
        if (_acked_message_by_sender.TryGetValue(remote_sender_id, out var acked_message_id) &&
            acked_message_id == clipboard_update.MessageId)
            return true;

        var message = new ClipboardMessage(
            ProtocolVersion,
            "clipboard",
            _channel,
            _sender_id,
            clipboard_update.MessageId,
            clipboard_update.Text);

        try
        {
            await connection.SendAsync(message, cancellation_token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Log($"Clipboard TCP write failed: {connection.RemoteAddress} ({exception.GetType().Name})");
            connection.Close();
            return false;
        }
    }

    private async Task ConnectionLoopAsync(CancellationToken cancellation_token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(ConnectionCheckIntervalMs));

        try
        {
            while (!cancellation_token.IsCancellationRequested)
            {
                EnsurePeerConnections(cancellation_token);
                await timer.WaitForNextTickAsync(cancellation_token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellation_token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(HeartbeatIntervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellation_token).ConfigureAwait(false))
            {
                var connections = _connections_by_sender.Values.ToArray();
                var heartbeat = new ClipboardMessage(
                    ProtocolVersion,
                    "ping",
                    _channel,
                    _sender_id,
                    null,
                    null);

                foreach (var connection in connections)
                {
                    var heartbeat_age = Environment.TickCount64 - connection.LastActivityAt;
                    if (heartbeat_age > HeartbeatTimeoutMs)
                    {
                        _logger.Log($"Clipboard TCP heartbeat timed out: {connection.RemoteAddress}");
                        connection.Close();
                        continue;
                    }

                    try
                    {
                        await connection.SendAsync(heartbeat, cancellation_token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        connection.Close();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void EnsurePeerConnections(CancellationToken cancellation_token)
    {
        var port = GetChannelPort(_channel);
        var targets = new Dictionary<string, PeerTarget>(StringComparer.Ordinal);

        var configured_addresses = _peer_addresses;
        foreach (var peer_address in configured_addresses)
        {
            var key = GetEndpointKey(peer_address, port);
            targets[key] = new PeerTarget(peer_address, port);
        }

        var discovered_peers = _discovered_peers_by_sender.Values.ToArray();
        foreach (var discovered_peer in discovered_peers)
        {
            var key = GetEndpointKey(discovered_peer.Address, discovered_peer.Port);
            targets[key] = new PeerTarget(discovered_peer.Address, discovered_peer.Port);
        }

        foreach (var target in targets.Values)
        {
            if (HasConnectionToAddress(target.Address)) continue;

            var endpoint_key = GetEndpointKey(target.Address, target.Port);
            if (!_connecting_endpoints.TryAdd(endpoint_key, 0)) continue;

            _ = ConnectPeerAsync(target, endpoint_key, cancellation_token);
        }
    }

    private bool HasConnectionToAddress(string peer_address)
    {
        var connections = _connections_by_sender.Values;
        return connections.Any(connection => connection.RemoteAddress == peer_address && !connection.IsClosed);
    }

    private async Task ConnectPeerAsync(
        PeerTarget target,
        string endpoint_key,
        CancellationToken cancellation_token)
    {
        try
        {
            using var connect_token_source = CancellationTokenSource.CreateLinkedTokenSource(cancellation_token);
            connect_token_source.CancelAfter(ConnectTimeoutMs);
            var tcp_client = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true
            };
            tcp_client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            try
            {
                await tcp_client.ConnectAsync(target.Address, target.Port, connect_token_source.Token).ConfigureAwait(false);
            }
            catch
            {
                tcp_client.Dispose();
                throw;
            }

            await RunPeerConnectionAsync(tcp_client, true, cancellation_token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation_token.IsCancellationRequested)
        {
            _logger.Log($"Clipboard TCP connection timed out: {target.Address}:{target.Port}");
        }
        catch (SocketException exception)
        {
            _logger.Log($"Clipboard TCP connection failed: {target.Address}:{target.Port} ({exception.SocketErrorCode})");
        }
        catch (Exception exception)
        {
            _logger.Log($"Clipboard TCP connection failed: {target.Address}:{target.Port} ({exception.GetType().Name})");
        }
        finally
        {
            _connecting_endpoints.TryRemove(endpoint_key, out _);
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellation_token)
    {
        try
        {
            while (!cancellation_token.IsCancellationRequested)
            {
                var tcp_client = await listener.AcceptTcpClientAsync(cancellation_token).ConfigureAwait(false);
                tcp_client.NoDelay = true;
                tcp_client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                _ = RunPeerConnectionAsync(tcp_client, false, cancellation_token);
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

    private async Task RunPeerConnectionAsync(
        TcpClient tcp_client,
        bool is_outbound,
        CancellationToken cancellation_token)
    {
        var remote_endpoint = tcp_client.Client.RemoteEndPoint as IPEndPoint;
        var remote_address = remote_endpoint?.Address.ToString() ?? string.Empty;
        var connection = new PeerConnection(tcp_client, remote_address, is_outbound);
        var was_registered = false;

        try
        {
            var hello_message = new ClipboardMessage(
                ProtocolVersion,
                "hello",
                _channel,
                _sender_id,
                null,
                null);
            await connection.SendAsync(hello_message, cancellation_token).ConfigureAwait(false);

            using var handshake_token_source = CancellationTokenSource.CreateLinkedTokenSource(cancellation_token);
            handshake_token_source.CancelAfter(HandshakeTimeoutMs);
            var remote_hello = await connection.ReadAsync(handshake_token_source.Token).ConfigureAwait(false);
            if (remote_hello is not { Version: ProtocolVersion, Type: "hello" } ||
                remote_hello.Channel != _channel || remote_hello.SenderId == _sender_id)
                return;

            connection.RemoteSenderId = remote_hello.SenderId;
            was_registered = RegisterConnection(connection);
            if (!was_registered) return;

            Report($"Clipboard TCP connected: {remote_address} ({(is_outbound ? "outbound" : "inbound")})");
            var latest_update = _latest_local_update;
            if (latest_update != null)
                await SendClipboardUpdateAsync(connection, latest_update, cancellation_token).ConfigureAwait(false);

            while (!cancellation_token.IsCancellationRequested && !connection.IsClosed)
            {
                var message = await connection.ReadAsync(cancellation_token).ConfigureAwait(false);
                connection.MarkActive();
                if (message.Version != ProtocolVersion || message.Channel != _channel ||
                    message.SenderId != connection.RemoteSenderId)
                    continue;

                await HandlePeerMessageAsync(connection, message, cancellation_token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException exception)
        {
            _logger.Log($"Clipboard TCP disconnected: {remote_address} ({exception.GetType().Name})");
        }
        catch (SocketException exception)
        {
            _logger.Log($"Clipboard TCP disconnected: {remote_address} ({exception.SocketErrorCode})");
        }
        catch (Exception exception)
        {
            _logger.Log($"Clipboard TCP connection error: {remote_address} ({exception.GetType().Name})");
        }
        finally
        {
            if (was_registered && connection.RemoteSenderId != null)
            {
                var item = new KeyValuePair<string, PeerConnection>(connection.RemoteSenderId, connection);
                _connections_by_sender.TryRemove(item);
            }

            connection.Close();
        }
    }

    private bool RegisterConnection(PeerConnection connection)
    {
        var remote_sender_id = connection.RemoteSenderId;
        if (remote_sender_id == null) return false;

        while (true)
        {
            if (_connections_by_sender.TryAdd(remote_sender_id, connection)) return true;
            if (!_connections_by_sender.TryGetValue(remote_sender_id, out var existing_connection)) continue;
            if (ReferenceEquals(existing_connection, connection)) return true;

            var prefer_outbound = string.CompareOrdinal(_sender_id, remote_sender_id) < 0;
            var new_is_preferred = connection.IsOutbound == prefer_outbound;
            var existing_is_preferred = existing_connection.IsOutbound == prefer_outbound;
            if (!new_is_preferred || existing_is_preferred) return false;

            if (!_connections_by_sender.TryUpdate(remote_sender_id, connection, existing_connection)) continue;

            existing_connection.Close();
            return true;
        }
    }

    private async Task HandlePeerMessageAsync(
        PeerConnection connection,
        ClipboardMessage message,
        CancellationToken cancellation_token)
    {
        if (message.Type == "clipboard")
        {
            await HandleClipboardMessageAsync(connection, message, cancellation_token).ConfigureAwait(false);
            return;
        }

        if (message.Type == "ack" && !string.IsNullOrWhiteSpace(message.MessageId) &&
            connection.RemoteSenderId != null)
        {
            _acked_message_by_sender[connection.RemoteSenderId] = message.MessageId;
            _logger.Log($"Clipboard delivered: {message.MessageId} to {connection.RemoteAddress}");
            return;
        }

        if (message.Type == "ping")
        {
            var pong_message = new ClipboardMessage(
                ProtocolVersion,
                "pong",
                _channel,
                _sender_id,
                null,
                null);
            await connection.SendAsync(pong_message, cancellation_token).ConfigureAwait(false);
        }
    }

    private async Task HandleClipboardMessageAsync(
        PeerConnection connection,
        ClipboardMessage message,
        CancellationToken cancellation_token)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId) || message.Text == null) return;

        var text_size = Encoding.UTF8.GetByteCount(message.Text);
        if (text_size > MaxTextBytes) return;

        if (!_received_message_ids.ContainsKey(message.MessageId))
        {
            await _poll_lock.WaitAsync(cancellation_token).ConfigureAwait(false);
            try
            {
                if (!_received_message_ids.ContainsKey(message.MessageId) &&
                    message.Text != _last_clipboard_text)
                {
                    _last_clipboard_text = message.Text;
                    _latest_local_update = null;
                    await _clipboard_text_writer(message.Text).ConfigureAwait(false);
                    RememberReceivedMessage(message.MessageId);
                    Report($"Clipboard received: {text_size} bytes from {connection.RemoteAddress}");
                }
                else if (!_received_message_ids.ContainsKey(message.MessageId))
                {
                    RememberReceivedMessage(message.MessageId);
                }
            }
            finally
            {
                _poll_lock.Release();
            }
        }

        var ack_message = new ClipboardMessage(
            ProtocolVersion,
            "ack",
            _channel,
            _sender_id,
            message.MessageId,
            null);
        await connection.SendAsync(ack_message, cancellation_token).ConfigureAwait(false);
    }

    private void RememberReceivedMessage(string message_id)
    {
        if (!_received_message_ids.TryAdd(message_id, 0)) return;

        _received_message_order.Enqueue(message_id);
        while (_received_message_order.Count > 256 && _received_message_order.TryDequeue(out var expired_message_id))
        {
            _received_message_ids.TryRemove(expired_message_id, out _);
        }
    }

    private void OnMdnsPeerDiscovered(MdnsClipboardPeer peer)
    {
        var is_new_peer = _discovered_peers_by_sender.TryAdd(peer.SenderId, peer);
        if (!is_new_peer)
            _discovered_peers_by_sender[peer.SenderId] = peer;

        if (is_new_peer)
            Report($"Clipboard mDNS peer discovered: {peer.Address}:{peer.Port}");

        var cancellation_token_source = _cancellation_token_source;
        if (cancellation_token_source != null)
            EnsurePeerConnections(cancellation_token_source.Token);
    }

    private static string GetEndpointKey(string address, int port)
    {
        return $"{address}:{port}";
    }

    private static async Task WriteMessageAsync(
        NetworkStream stream,
        ClipboardMessage message,
        CancellationToken cancellation_token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length <= 0 || payload.Length > MaxPayloadBytes)
            throw new InvalidDataException("Invalid clipboard TCP payload length.");

        var length_buffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length_buffer, payload.Length);
        await stream.WriteAsync(length_buffer, cancellation_token).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellation_token).ConfigureAwait(false);
    }

    private static async Task<ClipboardMessage> ReadMessageAsync(
        NetworkStream stream,
        CancellationToken cancellation_token)
    {
        var length_buffer = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(length_buffer, cancellation_token).ConfigureAwait(false);
        var payload_length = BinaryPrimitives.ReadInt32BigEndian(length_buffer);
        if (payload_length <= 0 || payload_length > MaxPayloadBytes)
            throw new InvalidDataException("Invalid clipboard TCP payload length.");

        var payload = new byte[payload_length];
        await stream.ReadExactlyAsync(payload, cancellation_token).ConfigureAwait(false);
        var message = JsonSerializer.Deserialize<ClipboardMessage>(payload);
        return message ?? throw new InvalidDataException("Invalid clipboard TCP message.");
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

    private sealed record ClipboardUpdate(string MessageId, string Text);

    private sealed record PeerTarget(string Address, int Port);

    private sealed record ClipboardMessage(
        int Version,
        string Type,
        string Channel,
        string SenderId,
        string? MessageId,
        string? Text);

    private sealed class PeerConnection
    {
        private readonly TcpClient _tcp_client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _send_lock = new(1, 1);
        private int _closed;
        private long _last_activity_at = Environment.TickCount64;

        public PeerConnection(TcpClient tcp_client, string remote_address, bool is_outbound)
        {
            _tcp_client = tcp_client;
            _stream = tcp_client.GetStream();
            RemoteAddress = remote_address;
            IsOutbound = is_outbound;
        }

        public string RemoteAddress { get; }

        public bool IsOutbound { get; }

        public string? RemoteSenderId { get; set; }

        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        public long LastActivityAt => Volatile.Read(ref _last_activity_at);

        public async Task SendAsync(ClipboardMessage message, CancellationToken cancellation_token)
        {
            await _send_lock.WaitAsync(cancellation_token).ConfigureAwait(false);
            try
            {
                if (IsClosed) throw new IOException("Clipboard TCP connection is closed.");

                await WriteMessageAsync(_stream, message, cancellation_token).ConfigureAwait(false);
            }
            finally
            {
                _send_lock.Release();
            }
        }

        public Task<ClipboardMessage> ReadAsync(CancellationToken cancellation_token)
        {
            return ReadMessageAsync(_stream, cancellation_token);
        }

        public void MarkActive()
        {
            Volatile.Write(ref _last_activity_at, Environment.TickCount64);
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;

            _tcp_client.Dispose();
        }
    }
}
