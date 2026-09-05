using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
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
    // Host and instance names can change: a probe that finds someone else already using them renames and
    // probes again (RFC 6762 9). Both are published as snapshots because the receive loop reads them.
    private volatile string _hostFqdn;
    private volatile IReadOnlyList<ServiceRecords> _services = [];
    private int _nameSuffix;                       // 0 = the configured name, 2 = "Name (2)" / "host-2", ...
    /// <summary>Set once probing has claimed the names; until then RFC 6762 8.1 forbids answering with them.</summary>
    private volatile bool _claimed;
    /// <summary>Raised by the receive loop when a probe sees someone else already using a name we proposed.</summary>
    private volatile bool _conflictSeen;
    /// <summary>Serialises renaming: a burst of conflicting records must cause one rename, not one each.</summary>
    private readonly object _renameLock = new();
    private bool _renameInFlight;
    // Published as an immutable snapshot: the receive loop, the announce timer and the network-change
    // handler all read it, and a Dictionary being cleared and repopulated underneath them is not safe.
    private volatile IReadOnlyDictionary<int, InterfaceInfo> _interfaces = new Dictionary<int, InterfaceInfo>();
    /// <summary>Multicast group memberships currently joined, so a refresh can add and drop the difference.</summary>
    private readonly HashSet<IPAddress> _joined = [];
    private readonly Dictionary<string, DateTime> _lastMulticast = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sendLock = new();
    private readonly Random _rng = new();
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private sealed record InterfaceInfo(int Index, string Name, List<IPAddress> Addresses);
    private sealed record ServiceRecords(string typeFqdn, string instanceFqdn, MdnsService svc, List<string> subtypeFqdns);

    public MdnsResponder(MdnsOptions options, ILogger logger)
    {
        _o = options;
        _log = logger;
        _hostFqdn = "";
        BuildNames(0);
    }

    /// <summary>
    /// (Re)builds the host name and the service instance names for the given rename suffix: 0 is the
    /// configured name, 2 gives "ImageWriter II (2)" and "imagewriter-ii-2.local", and so on. This is the
    /// DNS-SD convention (RFC 6763 9) and is what Bonjour shows users.
    /// </summary>
    private void BuildNames(int suffix)
    {
        _nameSuffix = suffix;
        string host = _o.HostLabel.TrimEnd('.');
        string instance = _o.InstanceName;
        if (suffix >= 2)
        {
            host = $"{host}-{suffix}";
            instance = $"{instance} ({suffix})";
        }
        _hostFqdn = host + ".local";
        var built = new List<ServiceRecords>();
        foreach (var svc in _o.Services)
        {
            string typeFqdn = svc.Type + ".local";
            string instanceFqdn = EscapeInstance(instance) + "." + typeFqdn;
            var subs = svc.Subtypes.Select(x => $"{x}._sub.{typeFqdn}").ToList();
            built.Add(new ServiceRecords(typeFqdn, instanceFqdn, svc, subs));
        }
        _services = built;
    }

    public string HostName => _hostFqdn;
    public IReadOnlyCollection<string> InstanceNames => _services.Select(s => s.instanceFqdn).ToList();
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>
    /// Escapes dots (RFC 6763 4.3) and clamps the label to the 63 bytes DNS allows. Throwing instead would
    /// take the whole responder down in <see cref="Start"/>, silently disabling discovery over a long name.
    /// </summary>
    private static string EscapeInstance(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length > 63)
        {
            int cut = 63;
            while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;   // do not split a UTF-8 sequence
            name = Encoding.UTF8.GetString(bytes, 0, cut);
        }
        return name.Replace("\\", "\\\\").Replace(".", "\\.");
    }

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
        _socket = socket;
        _claimed = false;
        SyncMemberships();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _log.LogInformation("mDNS responder started: host {Host}, services {Services}, interfaces {Interfaces}",
            _hostFqdn, string.Join(", ", _services.Select(s => s.instanceFqdn)), string.Join(", ", _interfaces.Values.Select(i => $"{i.Name}={string.Join("/", i.Addresses)}")));
    }

    public void Stop()
    {
        if (_socket is null) return;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        try { Announce(goodbye: true); } catch (Exception) { /* ignore */ }
        _cts?.Cancel();
        try { _socket.Close(); } catch (Exception) { /* ignore */ }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { /* ignore */ }
        _socket = null;
        _loop = null;
        _claimed = false;
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ interfaces

    private void RefreshInterfaces()
    {
        var found = new Dictionary<int, InterfaceInfo>();
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
            found[index] = new InterfaceInfo(index, nic.Name, addrs);
        }
        _interfaces = found;
    }

    /// <summary>
    /// Joins the multicast group on every current interface address and drops departed ones. Without this a
    /// responder that started before the network was up (a service beating DHCP to it), or that saw the NIC
    /// change afterwards, stays joined to nothing and never receives another query - discovery simply stops.
    /// </summary>
    private void SyncMemberships()
    {
        var socket = _socket;
        if (socket is null) return;
        var current = _interfaces.Values.SelectMany(i => i.Addresses).ToHashSet();
        int joinedCount;
        lock (_joined)
        {
            foreach (var addr in current.Except(_joined).ToList())
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, addr));
                    _joined.Add(addr);
                    _log.LogInformation("mDNS: joined the multicast group on {Address}", addr);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("mDNS: cannot join multicast group on {Address}: {Message}", addr, ex.Message);
                }
            }
            foreach (var addr in _joined.Except(current).ToList())
            {
                try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(Group, addr)); }
                catch (Exception) { /* the address is already gone; the membership goes with the socket */ }
                _joined.Remove(addr);
                _log.LogInformation("mDNS: left the multicast group on {Address}", addr);
            }
            joinedCount = _joined.Count;
        }
        if (joinedCount == 0)
            _log.LogWarning("mDNS: not joined on any interface; discovery is inactive until a network appears");
    }

    /// <summary>An address changed: re-enumerate, re-join and re-announce after letting the stack settle.</summary>
    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);   // debounce a burst of change events
                RefreshInterfaces();
                SyncMemberships();
                // RFC 6762 8.3: announce again once the address set has changed.
                for (int i = 0; i < 2 && !ct.IsCancellationRequested; i++)
                {
                    Announce();
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogDebug(ex, "mDNS network-change handling failed"); }
        }, ct);
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
            // The full record set does not fit in one datagram; RFC 6762 8.3 lets it span several, which is
            // better than emitting one oversized packet for IP to fragment.
            foreach (var packet in DnsWire.BuildResponses(0, records, [], legacy: false))
                SendMulticast(packet, iface);
        }
    }

    // ------------------------------------------------------------------ probing (RFC 6762 8.1, 9)

    /// <summary>
    /// Probes for the host name and every service instance name, renaming and retrying on conflict, until a
    /// set of names is unused on the network. Sets <see cref="_claimed"/> when the names are ours to use.
    /// </summary>
    private async Task ProbeAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 12 && !ct.IsCancellationRequested; attempt++)
        {
            _conflictSeen = false;
            try
            {
                // RFC 6762 8.1: wait a random 0-250 ms first, so simultaneously-booting devices do not
                // probe in lockstep, then send three probes 250 ms apart.
                await Task.Delay(_rng.Next(0, 250), ct);
                for (int i = 0; i < 3 && !_conflictSeen; i++)
                {
                    SendProbe();
                    await Task.Delay(250, ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "mDNS probe failed"); }

            if (!_conflictSeen)
            {
                _claimed = true;
                if (_nameSuffix >= 2)
                    _log.LogWarning("mDNS: name already in use on this network; renamed to host {Host}, instance {Instance}",
                        _hostFqdn, string.Join(", ", _services.Select(x => x.instanceFqdn)));
                else
                    _log.LogInformation("mDNS: probed and claimed {Host}", _hostFqdn);
                return;
            }

            NextName();
            _log.LogInformation("mDNS: name conflict during probing, retrying as {Host}", _hostFqdn);
        }
        // Give up defending and use the last candidate rather than never appearing at all.
        _claimed = true;
        _log.LogWarning("mDNS: still conflicting after repeated renames; continuing as {Host}", _hostFqdn);
    }

    /// <summary>Advances to the next candidate name: "Name" -> "Name (2)" -> "Name (3)" (RFC 6763 9).</summary>
    private void NextName() => BuildNames(_nameSuffix < 2 ? 2 : _nameSuffix + 1);

    /// <summary>
    /// One probe: a query for every name we intend to claim, QTYPE ANY, with the records we propose to use
    /// in the authority section so a simultaneous prober can tie-break against us (RFC 6762 8.2).
    /// </summary>
    private void SendProbe()
    {
        foreach (var iface in _interfaces.Values.ToList())
        {
            var names = new List<string> { _hostFqdn };
            names.AddRange(_services.Select(x => x.instanceFqdn));
            var proposed = new List<DnsRecord>();
            foreach (var (_, instanceFqdn, svc, _) in _services)
            {
                proposed.Add(DnsRecord.Srv(instanceFqdn, svc.Port, _hostFqdn, OtherTtl));
                proposed.Add(DnsRecord.Txt(instanceFqdn, svc.Txt, OtherTtl));
            }
            proposed.AddRange(iface.Addresses.Select(a => DnsRecord.AddressV4(_hostFqdn, a, OtherTtl)));
            foreach (var packet in DnsWire.BuildProbe(names, proposed))
                SendMulticast(packet, iface);
        }
    }

    /// <summary>
    /// Looks at an incoming response for records that clash with names we are probing for or already own:
    /// same name, same type, different rdata.
    ///
    /// Our own multicast comes back to us (MulticastLoopback is on), so echoes have to be filtered out — but
    /// NOT by source address. Two responders on one machine share every source address, so an address filter
    /// would hide exactly the conflict it is meant to catch. Comparing the rdata to what we would have sent
    /// is the discriminator that works in both cases: an echo matches ours exactly, a rival's does not.
    /// </summary>
    private void CheckForConflict(DnsPacket packet, IPEndPoint from)
    {
        var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _hostFqdn };
        foreach (var x in _services) mine.Add(x.instanceFqdn);

        foreach (var a in packet.Answers)
        {
            if (a.Type is not (DnsType.A or DnsType.SRV or DnsType.TXT)) continue;
            if (!mine.Contains(a.Name)) continue;
            if (OwnRecordMatches(a)) continue;            // identical data: not a conflict, just a duplicate view

            if (!_claimed)
            {
                _conflictSeen = true;
                return;
            }
            // RFC 6762 9: a conflict after claiming means someone else took the name; re-probe under a new one.
            lock (_renameLock)
            {
                if (_renameInFlight) return;      // a rename is already running; one conflict, one rename
                _renameInFlight = true;
            }
            _log.LogWarning("mDNS: {Name} ({Type}) is also claimed by {Peer}; renaming", a.Name, a.Type, from.Address);
            _claimed = false;
            var ct = _cts?.Token ?? CancellationToken.None;
            _ = Task.Run(async () =>
            {
                try
                {
                    NextName();
                    await ProbeAsync(ct);
                    Announce();
                }
                finally
                {
                    lock (_renameLock) _renameInFlight = false;
                }
            }, ct);
            return;
        }
    }

    /// <summary>True when the record carries exactly what we would have sent for that name and type.</summary>
    private bool OwnRecordMatches(DnsRecord incoming)
    {
        foreach (var iface in _interfaces.Values)
        {
            foreach (var r in AllRecords(iface, goodbye: false))
            {
                if (r.Type != incoming.Type || !r.Name.Equals(incoming.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (r.Data.AsSpan().SequenceEqual(incoming.Data)) return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ receive loop

    private async Task LoopAsync(CancellationToken ct)
    {
        var socket = _socket!;
        var buffer = new byte[9000];
        var announceTimer = Task.Run(async () =>
        {
            // RFC 6762 8.1: probe before claiming any unique record, so we never fight another responder
            // for a name. Only after that may we announce or answer with these names.
            await ProbeAsync(ct);
            // RFC 6762 8.3: announce at least twice, one second apart; repeat occasionally for clients that lost cache.
            for (int i = 0; i < 3 && !ct.IsCancellationRequested; i++)
            {
                try { Announce(); } catch (Exception ex) { _log.LogDebug(ex, "mDNS announce failed"); }
                await Task.Delay(TimeSpan.FromSeconds(1 << i), ct);
            }
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(30), ct);
                try { RefreshInterfaces(); SyncMemberships(); Announce(); } catch (Exception ex) { _log.LogDebug(ex, "mDNS re-announce failed"); }
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
        if (!packet.IsQuery)
        {
            // Responses are where name conflicts show up; ignoring them is why conflicts used to be invisible.
            CheckForConflict(packet, from);
            return;
        }
        if (packet.Questions.Count == 0) return;
        // RFC 6762 8.1: until probing has claimed the names, do not answer with them.
        if (!_claimed) return;

        var interfaces = _interfaces;
        if (!interfaces.TryGetValue(interfaceIndex, out var iface))
        {
            RefreshInterfaces();
            SyncMemberships();
            interfaces = _interfaces;
            if (!interfaces.TryGetValue(interfaceIndex, out iface))
            {
                // Unknown interface (e.g. loopback delivery): answer with every address we have.
                iface = new InterfaceInfo(interfaceIndex, "any", interfaces.Values.SelectMany(i => i.Addresses).Distinct().ToList());
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
            foreach (var packetBytes in DnsWire.BuildResponses(packet.Id, answers, additionals, legacy: true))
                SendUnicast(packetBytes, from, iface);
            return;
        }

        var responses = DnsWire.BuildResponses(0, answers, additionals, legacy: false);
        if (wantUnicast) foreach (var r in responses) SendUnicast(r, from, iface);

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
        if (delay == 0)
        {
            foreach (var r in responses) SendMulticast(r, iface);
        }
        else
        {
            var capturedIface = iface;
            _ = Task.Delay(delay).ContinueWith(_ =>
            {
                foreach (var r in responses) SendMulticast(r, capturedIface);
            }, TaskScheduler.Default);
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
