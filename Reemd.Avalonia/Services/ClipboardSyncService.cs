using System.Net;
using System.Net.NetworkInformation;
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
    private readonly List<UdpClient> _udpClients = [];
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

            var endpoint = GetChannelEndpoint(_channel);
            var localAddresses = GetLocalMulticastAddresses();
            foreach (var localAddress in localAddresses)
            {
                TryAddMulticastClient(endpoint, localAddress);
            }

            if (_udpClients.Count == 0)
            {
                Report("Clipboard listener error: no active IPv4 multicast interface");
                return;
            }

            var cancellationTokenSource = new CancellationTokenSource();
            _cancellationTokenSource = cancellationTokenSource;

            foreach (var udpClient in _udpClients)
            {
                _ = ReceiveLoopAsync(udpClient, cancellationTokenSource.Token);
            }
            _ = PollClipboardLoopAsync(cancellationTokenSource.Token);

            var activeInterfaceAddresses = _udpClients
                .Select(udpClient => ((IPEndPoint)udpClient.Client.LocalEndPoint!).Address);
            var interfaceNames = string.Join(", ", activeInterfaceAddresses);
            Report($"Clipboard listening: {_channel} ({endpoint.Address}:{endpoint.Port}) via {interfaceNames}");
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
            _cancellationTokenSource = null;
            var udpClients = _udpClients.ToArray();
            _udpClients.Clear();

            cancellationTokenSource?.Cancel();
            foreach (var udpClient in udpClients)
            {
                udpClient.Dispose();
            }
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

            UdpClient[] udpClients;
            lock (_lifecycleLock)
            {
                udpClients = _udpClients.ToArray();
            }
            if (udpClients.Length == 0) return;

            var endpoint = GetChannelEndpoint(_channel);
            var sentCount = 0;
            foreach (var udpClient in udpClients)
            {
                try
                {
                    await udpClient.SendAsync(payload, endpoint, cancellationToken).ConfigureAwait(false);
                    sentCount++;
                }
                catch (SocketException exception)
                {
                    _logger.Log($"Clipboard send failed on {udpClient.Client.LocalEndPoint}: {exception.SocketErrorCode}");
                }
            }

            if (sentCount > 0)
                Report($"Clipboard sent: {payload.Length} bytes on {_channel} via {sentCount} interface(s)");
            else
                Report("Clipboard send error: no active interface could reach the multicast group");
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

    private void TryAddMulticastClient(IPEndPoint endpoint, IPAddress localAddress)
    {
        UdpClient? udpClient = null;
        try
        {
            udpClient = new UdpClient(AddressFamily.InterNetwork);
            udpClient.ExclusiveAddressUse = false;
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(localAddress, endpoint.Port));
            udpClient.JoinMulticastGroup(endpoint.Address, localAddress);
            udpClient.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
            udpClient.MulticastLoopback = true;
            _udpClients.Add(udpClient);
        }
        catch (SocketException exception)
        {
            udpClient?.Dispose();
            _logger.Log($"Clipboard interface unavailable: {localAddress} ({exception.SocketErrorCode})");
        }
    }

    private static IPAddress[] GetLocalMulticastAddresses()
    {
        var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        var eligibleInterfaces = networkInterfaces
            .Where(networkInterface =>
                networkInterface.OperationalStatus == OperationalStatus.Up &&
                networkInterface.SupportsMulticast &&
                networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback);
        var unicastAddresses = eligibleInterfaces
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses);
        var ipv4Addresses = unicastAddresses
            .Select(unicastAddress => unicastAddress.Address)
            .Where(address =>
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address) &&
                !address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        var localAddresses = ipv4Addresses.Distinct().ToArray();
        return localAddresses;
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
