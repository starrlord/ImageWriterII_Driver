using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace ImageWriterII.Ipp.Discovery;

/// <summary>One DNS-SD service to advertise (e.g. _ipp._tcp on port 631 with a TXT record).</summary>
public sealed class MdnsService
{
    public required string Type { get; init; } // "_ipp._tcp"
    public required ushort Port { get; init; }
    public List<string> Subtypes { get; init; } = []; // "_universal", "_print"
    public List<KeyValuePair<string, string>> Txt { get; init; } = [];
}

public sealed class MdnsOptions
{
    public required string InstanceName { get; init; } // "ImageWriter II"
    public required string HostLabel { get; init; }    // "imagewriter-ii"
    public List<MdnsService> Services { get; init; } = [];
    /// <summary>Only use interfaces whose name/description contains one of these (empty = all eligible).</summary>
    public List<string> InterfaceFilter { get; init; } = [];
}

/// <summary>
/// Multicast DNS / DNS-SD responder (RFC 6762 / 6763) that makes the printer discoverable by
/// Windows ("Add a printer"), macOS/iOS (AirPrint) and Linux (CUPS/Avahi) without depending on Bonjour.
/// IPv4 only; one socket bound to 0.0.0.0:5353 joined to the group on every eligible interface,
/// answering with the address of the interface a query arrived on.
/// </summary>
public sealed class MdnsResponder : IDisposable
{
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint GroupEndPoint = new(Group, 5353);
    private const uint PtrTtl = 4500;
    private const uint OtherTtl = 120;

    private readonly MdnsOptions _o;
    private readonly ILogger _log;
    private readonly string _hostFqdn;
    private readonly List<(string typeFqdn, string instanceFqdn, MdnsService svc, List<string> subtypeFqdns)> _services = [];
    private readonly Dictionary<int, InterfaceInfo> _interfaces = new();
    private readonly Dictionary<string, DateTime> _lastMulticast = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sendLock = new();
    private readonly Random _rng = new();
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private sealed record InterfaceInfo(int Index, string Name, List<IPAddress> Addresses);

    public MdnsResponder(MdnsOptions options, ILogger logger)
    {
        _o = options;
        _log = logger;
        _hostFqdn = options.HostLabel.TrimEnd('.') + ".local";
        foreach (var svc in options.Services)
        {
            string typeFqdn = svc.Type + ".local";
            string instanceFqdn = EscapeInstance(options.InstanceName) + "." + typeFqdn;
            var subs = svc.Subtypes.Select(s => $"{s}._sub.{typeFqdn}").ToList();
            _services.Add((typeFqdn, instanceFqdn, svc, subs));
        }
    }

    public string HostName => _hostFqdn;
    public IReadOnlyCollection<string> InstanceNames => _services.Select(s => s.instanceFqdn).ToList();
    public bool IsRunning => _loop is { IsCompleted: false };

    private static string EscapeInstance(string name) => name.Replace(".", "\\.");

    public void Start()
    {
        if (_loop is not null) return;
        RefreshInterfaces();
        if (_interfaces.Count == 0)
        {
            _log.LogWarning("mDNS: no eligible IPv4 network interfaces found; discovery disabled until one appears");
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
        foreach (var i in _interfaces.Values)
        {
            foreach (var addr in i.Addresses)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, addr));
                }
                catch (SocketException ex)
                {
                    _log.LogWarning("mDNS: cannot join multicast group on {Interface} ({Address}): {Message}", i.Name, addr, ex.Message);
                }
            }
        }
        _socket = socket;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _log.LogInformation("mDNS responder started: host {Host}, services {Services}, interfaces {Interfaces}",
            _hostFqdn, string.Join(", ", _services.Select(s => s.instanceFqdn)), string.Join(", ", _interfaces.Values.Select(i => $"{i.Name}={string.Join("/", i.Addresses)}")));
    }

    public void Stop()
    {
        if (_socket is null) return;
        try { Announce(goodbye: true); } catch (Exception) { /* ignore */ }
        _cts?.Cancel();
        try { _socket.Close(); } catch (Exception) { /* ignore */ }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { /* ignore */ }
        _socket = null;
        _loop = null;
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ interfaces

    private void RefreshInterfaces()
    {
        _interfaces.Clear();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) continue;
            if (!nic.Supports(NetworkInterfaceComponent.IPv4)) continue;
            if (_o.InterfaceFilter.Count > 0 && !_o.InterfaceFilter.Any(f => nic.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || nic.Description.Contains(f, StringComparison.OrdinalIgnoreCase)))
                continue;
            var props = nic.GetIPProperties();
            var addrs = props.UnicastAddresses
                .Select(u => u.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(a))
                .ToList();
            if (addrs.Count == 0) continue;
            int index;
            try { index = props.GetIPv4Properties().Index; } catch (Exception) { continue; }
            _interfaces[index] = new InterfaceInfo(index, nic.Name, addrs);
        }
    }

    private static bool IsLinkLocal(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    // ------------------------------------------------------------------ records

    private IEnumerable<DnsRecord> AllRecords(InterfaceInfo iface, bool goodbye)
    {
        uint ptrTtl = goodbye ? 0 : PtrTtl, ttl = goodbye ? 0 : OtherTtl;
        foreach (var (typeFqdn, instanceFqdn, svc, subs) in _services)
        {
            yield return DnsRecord.Ptr("_services._dns-sd._udp.local", typeFqdn, ptrTtl);
            yield return DnsRecord.Ptr(typeFqdn, instanceFqdn, ptrTtl);
            foreach (var sub in subs) yield return DnsRecord.Ptr(sub, instanceFqdn, ptrTtl);
            yield return DnsRecord.Srv(instanceFqdn, svc.Port, _hostFqdn, ttl);
            yield return DnsRecord.Txt(instanceFqdn, svc.Txt, ttl);
        }
        foreach (var a in iface.Addresses) yield return DnsRecord.AddressV4(_hostFqdn, a, ttl);
    }

    private List<DnsRecord> AddressRecords(InterfaceInfo iface) =>
        iface.Addresses.Select(a => DnsRecord.AddressV4(_hostFqdn, a, OtherTtl)).ToList();

    /// <summary>Send unsolicited announcements (or goodbyes, TTL 0) on every interface.</summary>
    public void Announce(bool goodbye = false)
    {
        foreach (var iface in _interfaces.Values.ToList())
        {
            var records = AllRecords(iface, goodbye).ToList();
            var packet = DnsWire.BuildResponse(0, records, [], legacy: false);
            SendMulticast(packet, iface);
        }
    }

    // ------------------------------------------------------------------ receive loop

    private async Task LoopAsync(CancellationToken ct)
    {
        var socket = _socket!;
        var buffer = new byte[9000];
        var announceTimer = Task.Run(async () =>
        {
            // RFC 6762 8.3: announce at least twice, one second apart; repeat occasionally for clients that lost cache.
            for (int i = 0; i < 3 && !ct.IsCancellationRequested; i++)
            {
                try { Announce(); } catch (Exception ex) { _log.LogDebug(ex, "mDNS announce failed"); }
                await Task.Delay(TimeSpan.FromSeconds(1 << i), ct);
            }
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(30), ct);
                try { RefreshInterfaces(); Announce(); } catch (Exception ex) { _log.LogDebug(ex, "mDNS re-announce failed"); }
            }
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult result;
            try
            {
                result = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                _log.LogDebug("mDNS receive error: {Message}", ex.Message);
                await Task.Delay(100, ct);
                continue;
            }
            try
            {
                HandlePacket(buffer.AsSpan(0, result.ReceivedBytes), (IPEndPoint)result.RemoteEndPoint, result.PacketInformation.Interface);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "mDNS: bad packet from {Remote}", result.RemoteEndPoint);
            }
        }
        try { await announceTimer; } catch (OperationCanceledException) { }
    }

    private void HandlePacket(ReadOnlySpan<byte> data, IPEndPoint from, int interfaceIndex)
    {
        var packet = DnsWire.Parse(data);
        if (!packet.IsQuery || packet.Questions.Count == 0) return;

        if (!_interfaces.TryGetValue(interfaceIndex, out var iface))
        {
            RefreshInterfaces();
            if (!_interfaces.TryGetValue(interfaceIndex, out iface))
            {
                // Unknown interface (e.g. loopback delivery): answer with every address we have.
                iface = new InterfaceInfo(interfaceIndex, "any", _interfaces.Values.SelectMany(i => i.Addresses).Distinct().ToList());
                if (iface.Addresses.Count == 0) return;
            }
        }

        var answers = new List<DnsRecord>();
        var additionals = new List<DnsRecord>();
        bool shared = false;
        bool wantUnicast = false;
        bool legacy = from.Port != 5353;

        foreach (var q in packet.Questions)
        {
            if (q.Class != 1 && q.Class != 255) continue;
            string name = q.Name;
            if (q.UnicastResponse) wantUnicast = true;

            if (q.Type is DnsType.PTR or DnsType.ANY)
            {
                if (name.Equals("_services._dns-sd._udp.local", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var s in _services)
                        if (!KnownAnswer(packet, "_services._dns-sd._udp.local", s.typeFqdn))
                            answers.Add(DnsRecord.Ptr("_services._dns-sd._udp.local", s.typeFqdn, PtrTtl));
                    shared = true;
                }
                foreach (var s in _services)
                {
                    bool match = name.Equals(s.typeFqdn, StringComparison.OrdinalIgnoreCase) || s.subtypeFqdns.Any(sub => sub.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (!match) continue;
                    shared = true;
                    if (KnownAnswer(packet, name, s.instanceFqdn)) continue;
                    answers.Add(DnsRecord.Ptr(name, s.instanceFqdn, PtrTtl));
                    AddIfMissing(additionals, DnsRecord.Srv(s.instanceFqdn, s.svc.Port, _hostFqdn, OtherTtl));
                    AddIfMissing(additionals, DnsRecord.Txt(s.instanceFqdn, s.svc.Txt, OtherTtl));
                    foreach (var a in AddressRecords(iface)) AddIfMissing(additionals, a);
                }
            }

            foreach (var s in _services)
            {
                if (!name.Equals(s.instanceFqdn, StringComparison.OrdinalIgnoreCase)) continue;
                if (q.Type is DnsType.SRV or DnsType.ANY)
                {
                    AddIfMissing(answers, DnsRecord.Srv(s.instanceFqdn, s.svc.Port, _hostFqdn, OtherTtl));
                    foreach (var a in AddressRecords(iface)) AddIfMissing(additionals, a);
                }
                if (q.Type is DnsType.TXT or DnsType.ANY)
                    AddIfMissing(answers, DnsRecord.Txt(s.instanceFqdn, s.svc.Txt, OtherTtl));
            }

            if (name.Equals(_hostFqdn, StringComparison.OrdinalIgnoreCase))
            {
                if (q.Type is DnsType.A or DnsType.ANY)
                    foreach (var a in AddressRecords(iface)) AddIfMissing(answers, a);
                if (q.Type is DnsType.AAAA or DnsType.A or DnsType.ANY)
                    AddIfMissing(additionals, DnsRecord.Nsec(_hostFqdn, OtherTtl, DnsType.A));
            }
        }

        if (answers.Count == 0) return;
        // don't repeat additionals that are already answers
        additionals.RemoveAll(a => answers.Any(x => x.Type == a.Type && x.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)));

        if (legacy)
        {
            var packetBytes = DnsWire.BuildResponse(packet.Id, answers, additionals, legacy: true);
            SendUnicast(packetBytes, from, iface);
            return;
        }

        var response = DnsWire.BuildResponse(0, answers, additionals, legacy: false);
        if (wantUnicast) SendUnicast(response, from, iface);

        // Rate-limit multicast replies for the same records to once per second (RFC 6762 6).
        string key = iface.Index + ":" + string.Join("|", answers.Select(a => a.Type + a.Name).OrderBy(x => x));
        lock (_lastMulticast)
        {
            if (_lastMulticast.TryGetValue(key, out var last) && (DateTime.UtcNow - last).TotalMilliseconds < 1000 && !wantUnicast) return;
            _lastMulticast[key] = DateTime.UtcNow;
            if (_lastMulticast.Count > 256) _lastMulticast.Clear();
        }
        if (wantUnicast && !shared) return;

        int delay = shared ? _rng.Next(20, 120) : 0;
        if (delay == 0) SendMulticast(response, iface);
        else
        {
            var capturedIface = iface;
            _ = Task.Delay(delay).ContinueWith(_ => SendMulticast(response, capturedIface), TaskScheduler.Default);
        }
    }

    private static bool KnownAnswer(DnsPacket packet, string name, string target) =>
        packet.Answers.Any(a => a.Type == DnsType.PTR && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                 && a.PtrTarget is not null && a.PtrTarget.Equals(target, StringComparison.OrdinalIgnoreCase)
                                 && a.Ttl > PtrTtl / 2);

    private static void AddIfMissing(List<DnsRecord> list, DnsRecord r)
    {
        if (!list.Any(x => x.Type == r.Type && x.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase) && x.Data.AsSpan().SequenceEqual(r.Data)))
            list.Add(r);
    }

    // ------------------------------------------------------------------ sending

    private void SendMulticast(byte[] packet, InterfaceInfo iface)
    {
        var socket = _socket;
        if (socket is null) return;
        lock (_sendLock)
        {
            foreach (var addr in iface.Addresses.Take(1))
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());
                    socket.SendTo(packet, GroupEndPoint);
                }
                catch (SocketException ex)
                {
                    _log.LogDebug("mDNS send on {Interface} failed: {Message}", iface.Name, ex.Message);
                }
                catch (ObjectDisposedException) { }
            }
        }
    }

    private void SendUnicast(byte[] packet, IPEndPoint to, InterfaceInfo iface)
    {
        var socket = _socket;
        if (socket is null) return;
        lock (_sendLock)
        {
            try { socket.SendTo(packet, to); }
            catch (SocketException ex) { _log.LogDebug("mDNS unicast to {To} failed: {Message}", to, ex.Message); }
            catch (ObjectDisposedException) { }
        }
    }
}
