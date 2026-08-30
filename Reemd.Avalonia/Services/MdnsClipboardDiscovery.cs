using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Reemd.Services;

/// <summary>
/// Advertises and discovers ReeMD clipboard peers through multicast DNS.
/// </summary>
public sealed class MdnsClipboardDiscovery : IDisposable
{
    private const string ServiceType = "_reemd-clipboard._tcp.local";
    private const int MdnsPort = 5353;
    private const int AnnouncementIntervalSeconds = 15;
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");

    private readonly string _channel;
    private readonly string _sender_id;
    private readonly int _service_port;
    private readonly Action<MdnsClipboardPeer> _peer_discovered;
    private readonly Action<string> _log;
    private UdpClient? _udp_client;
    private CancellationTokenSource? _cancellation_token_source;

    public MdnsClipboardDiscovery(
        string channel,
        string sender_id,
        int service_port,
        Action<MdnsClipboardPeer> peer_discovered,
        Action<string> log)
    {
        _channel = channel;
        _sender_id = sender_id;
        _service_port = service_port;
        _peer_discovered = peer_discovered;
        _log = log;
    }

    public void Start()
    {
        if (_cancellation_token_source != null) return;

        var udp_client = new UdpClient(AddressFamily.InterNetwork);
        udp_client.ExclusiveAddressUse = false;
        udp_client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp_client.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));

        var local_addresses = GetLocalAddresses();
        if (local_addresses.Length == 0)
        {
            udp_client.Dispose();
            throw new InvalidOperationException("No active IPv4 multicast interface is available.");
        }

        foreach (var local_address in local_addresses)
        {
            udp_client.JoinMulticastGroup(MulticastAddress, local_address);
        }

        var cancellation_token_source = new CancellationTokenSource();
        _udp_client = udp_client;
        _cancellation_token_source = cancellation_token_source;
        _ = ReceiveLoopAsync(udp_client, cancellation_token_source.Token);
        _ = AnnounceLoopAsync(udp_client, cancellation_token_source.Token);
    }

    private async Task AnnounceLoopAsync(UdpClient udp_client, CancellationToken cancellation_token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(AnnouncementIntervalSeconds));

        try
        {
            await SendAnnouncementAsync(udp_client, cancellation_token).ConfigureAwait(false);
            await SendQueryAsync(udp_client, cancellation_token).ConfigureAwait(false);

            while (await timer.WaitForNextTickAsync(cancellation_token).ConfigureAwait(false))
            {
                await SendAnnouncementAsync(udp_client, cancellation_token).ConfigureAwait(false);
                await SendQueryAsync(udp_client, cancellation_token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _log($"Clipboard mDNS announce error: {exception.GetType().Name}");
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp_client, CancellationToken cancellation_token)
    {
        try
        {
            while (!cancellation_token.IsCancellationRequested)
            {
                var result = await udp_client.ReceiveAsync(cancellation_token).ConfigureAwait(false);
                if (result.RemoteEndPoint.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                try
                {
                    if (ContainsServiceQuery(result.Buffer))
                        await SendAnnouncementAsync(udp_client, cancellation_token).ConfigureAwait(false);

                    var peer = TryParsePeer(result.Buffer, result.RemoteEndPoint.Address);
                    if (peer == null || peer.SenderId == _sender_id || peer.Channel != _channel) continue;

                    _peer_discovered(peer);
                }
                catch (InvalidDataException)
                {
                    _log($"Clipboard mDNS ignored malformed packet from {result.RemoteEndPoint.Address}");
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
            _log($"Clipboard mDNS receive error: {exception.SocketErrorCode}");
        }
        catch (Exception exception)
        {
            _log($"Clipboard mDNS receive error: {exception.GetType().Name}");
        }
    }

    private async Task SendAnnouncementAsync(UdpClient udp_client, CancellationToken cancellation_token)
    {
        var local_addresses = GetLocalAddresses();
        var packet = BuildAnnouncement(local_addresses);
        var endpoint = new IPEndPoint(MulticastAddress, MdnsPort);
        await udp_client.SendAsync(packet, endpoint, cancellation_token).ConfigureAwait(false);
    }

    private static async Task SendQueryAsync(UdpClient udp_client, CancellationToken cancellation_token)
    {
        var packet = BuildQuery();
        var endpoint = new IPEndPoint(MulticastAddress, MdnsPort);
        await udp_client.SendAsync(packet, endpoint, cancellation_token).ConfigureAwait(false);
    }

    private byte[] BuildAnnouncement(IPAddress[] local_addresses)
    {
        using var stream = new MemoryStream();
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0x8400);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, (ushort)(3 + local_addresses.Length));
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);

        var instance_name = $"ReeMD-{_sender_id}.{ServiceType}";
        var host_name = $"reemd-{_sender_id}.local";
        var ptr_data = BuildNameData(instance_name);
        WriteRecord(stream, ServiceType, 12, 1, 120, ptr_data);

        using var srv_stream = new MemoryStream();
        WriteUInt16(srv_stream, 0);
        WriteUInt16(srv_stream, 0);
        WriteUInt16(srv_stream, (ushort)_service_port);
        WriteName(srv_stream, host_name);
        var srv_data = srv_stream.ToArray();
        WriteRecord(stream, instance_name, 33, 0x8001, 120, srv_data);

        var txt_data = BuildTxtData($"channel={_channel}", $"sender={_sender_id}");
        WriteRecord(stream, instance_name, 16, 0x8001, 120, txt_data);

        foreach (var local_address in local_addresses)
        {
            var address_data = local_address.GetAddressBytes();
            WriteRecord(stream, host_name, 1, 0x8001, 120, address_data);
        }

        return stream.ToArray();
    }

    private static byte[] BuildQuery()
    {
        using var stream = new MemoryStream();
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteName(stream, ServiceType);
        WriteUInt16(stream, 12);
        WriteUInt16(stream, 1);
        return stream.ToArray();
    }

    private static bool ContainsServiceQuery(byte[] packet)
    {
        if (packet.Length < 12) return false;

        var question_count = ReadUInt16(packet, 4);
        var offset = 12;
        for (var index = 0; index < question_count; index++)
        {
            var name = ReadName(packet, ref offset);
            if (offset + 4 > packet.Length) return false;

            var type = ReadUInt16(packet, offset);
            offset += 4;
            if (type == 12 && string.Equals(name, ServiceType, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static MdnsClipboardPeer? TryParsePeer(byte[] packet, IPAddress source_address)
    {
        if (packet.Length < 12) return null;

        var question_count = ReadUInt16(packet, 4);
        var answer_count = ReadUInt16(packet, 6);
        var authority_count = ReadUInt16(packet, 8);
        var additional_count = ReadUInt16(packet, 10);
        var offset = 12;

        for (var index = 0; index < question_count; index++)
        {
            ReadName(packet, ref offset);
            if (offset + 4 > packet.Length) return null;
            offset += 4;
        }

        string? channel = null;
        string? sender_id = null;
        var service_port = 0;
        var record_count = answer_count + authority_count + additional_count;
        for (var index = 0; index < record_count; index++)
        {
            var record_name = ReadName(packet, ref offset);
            if (offset + 10 > packet.Length) return null;

            var type = ReadUInt16(packet, offset);
            var data_length = ReadUInt16(packet, offset + 8);
            offset += 10;
            if (offset + data_length > packet.Length) return null;

            var is_service_record = record_name.EndsWith(ServiceType, StringComparison.OrdinalIgnoreCase);
            if (is_service_record && type == 16)
            {
                var values = ReadTxtValues(packet, offset, data_length);
                values.TryGetValue("channel", out channel);
                values.TryGetValue("sender", out sender_id);
            }
            else if (is_service_record && type == 33 && data_length >= 6)
            {
                service_port = ReadUInt16(packet, offset + 4);
            }

            offset += data_length;
        }

        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(sender_id) || service_port <= 0)
            return null;

        return new MdnsClipboardPeer(sender_id, channel, source_address.ToString(), service_port);
    }

    private static Dictionary<string, string> ReadTxtValues(byte[] packet, int offset, int data_length)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var end_offset = offset + data_length;
        while (offset < end_offset)
        {
            var value_length = packet[offset];
            offset++;
            if (offset + value_length > end_offset) break;

            var value = Encoding.UTF8.GetString(packet, offset, value_length);
            offset += value_length;
            var separator_index = value.IndexOf('=');
            if (separator_index <= 0) continue;

            var key = value[..separator_index];
            var item_value = value[(separator_index + 1)..];
            values[key] = item_value;
        }

        return values;
    }

    private static byte[] BuildNameData(string name)
    {
        using var stream = new MemoryStream();
        WriteName(stream, name);
        return stream.ToArray();
    }

    private static byte[] BuildTxtData(params string[] values)
    {
        using var stream = new MemoryStream();
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > byte.MaxValue)
                throw new InvalidDataException("mDNS TXT value is too long.");

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        return stream.ToArray();
    }

    private static void WriteRecord(
        Stream stream,
        string name,
        ushort type,
        ushort record_class,
        uint ttl,
        byte[] data)
    {
        WriteName(stream, name);
        WriteUInt16(stream, type);
        WriteUInt16(stream, record_class);
        WriteUInt32(stream, ttl);
        WriteUInt16(stream, (ushort)data.Length);
        stream.Write(data);
    }

    private static void WriteName(Stream stream, string name)
    {
        var normalized_name = name.TrimEnd('.');
        var labels = normalized_name.Split('.');
        foreach (var label in labels)
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length == 0 || bytes.Length > 63)
                throw new InvalidDataException("Invalid mDNS name label.");

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
    }

    private static string ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        var current_offset = offset;
        var jumped = false;
        var jumps = 0;

        while (current_offset < packet.Length)
        {
            var length = packet[current_offset];
            if (length == 0)
            {
                current_offset++;
                if (!jumped) offset = current_offset;
                return string.Join('.', labels);
            }

            if ((length & 0xc0) == 0xc0)
            {
                if (current_offset + 1 >= packet.Length || jumps++ > 16)
                    throw new InvalidDataException("Invalid compressed mDNS name.");

                var pointer = ((length & 0x3f) << 8) | packet[current_offset + 1];
                if (!jumped) offset = current_offset + 2;
                current_offset = pointer;
                jumped = true;
                continue;
            }

            current_offset++;
            if (current_offset + length > packet.Length)
                throw new InvalidDataException("Invalid mDNS name.");

            var label = Encoding.UTF8.GetString(packet, current_offset, length);
            labels.Add(label);
            current_offset += length;
        }

        throw new InvalidDataException("Unterminated mDNS name.");
    }

    private static IPAddress[] GetLocalAddresses()
    {
        var network_interfaces = NetworkInterface.GetAllNetworkInterfaces();
        var addresses = new List<IPAddress>();
        foreach (var network_interface in network_interfaces)
        {
            if (network_interface.OperationalStatus != OperationalStatus.Up ||
                network_interface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                !network_interface.SupportsMulticast)
                continue;

            var properties = network_interface.GetIPProperties();
            foreach (var unicast_address in properties.UnicastAddresses)
            {
                var address = unicast_address.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
                if (address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) continue;
                if (!addresses.Contains(address)) addresses.Add(address);
            }
        }

        return addresses.ToArray();
    }

    private static ushort ReadUInt16(byte[] packet, int offset)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, sizeof(ushort)));
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        var buffer = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        var buffer = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    public void Dispose()
    {
        var cancellation_token_source = _cancellation_token_source;
        var udp_client = _udp_client;
        _cancellation_token_source = null;
        _udp_client = null;
        cancellation_token_source?.Cancel();
        udp_client?.Dispose();
        cancellation_token_source?.Dispose();
    }
}

public sealed record MdnsClipboardPeer(string SenderId, string Channel, string Address, int Port);
