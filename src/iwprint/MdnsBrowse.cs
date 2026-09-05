using System.Net;
using System.Net.Sockets;
using System.Text;
using ImageWriterII.Ipp.Discovery;

namespace ImageWriterII.Cli;

/// <summary>Sends an mDNS PTR query for a service type and prints every answer for a few seconds (a tiny dns-sd -B).</summary>
public static class MdnsBrowse
{
    public static int Run(string serviceType, TimeSpan wait)
    {
        string name = serviceType.TrimEnd('.');
        if (!name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) name += ".local";

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0)); // legacy (non-5353) source port: responders answer unicast
        socket.ReceiveTimeout = 500;

        var query = new List<byte>();
        query.AddRange(new byte[] { 0x12, 0x34, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        query.AddRange(DnsWire.EncodeName(name));
        query.AddRange(new byte[] { 0x00, 0x0C, 0x00, 0x01 }); // PTR IN
        var group = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        socket.SendTo(query.ToArray(), group);
        Console.WriteLine($"browsing {name} for {wait.TotalSeconds:0}s ...");

        var buf = new byte[9000];
        var deadline = DateTime.UtcNow + wait;
        var seen = new HashSet<string>();
        int packets = 0;
        while (DateTime.UtcNow < deadline)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try { n = socket.ReceiveFrom(buf, ref from); }
            catch (SocketException) { continue; }
            packets++;
            DnsPacket p;
            try { p = DnsWire.Parse(buf.AsSpan(0, n)); } catch (Exception) { continue; }
            foreach (var a in p.Answers)
            {
                string line = Describe(a, buf.AsSpan(0, n));
                if (seen.Add(line)) Console.WriteLine($"  from {from}: {line}");
            }
        }
        Console.WriteLine($"{packets} packet(s) received, {seen.Count} distinct record(s).");
        return seen.Count > 0 ? 0 : 2;
    }

    private static string Describe(DnsRecord r, ReadOnlySpan<byte> packet)
    {
        switch (r.Type)
        {
            case DnsType.PTR: return $"PTR {r.Name} -> {r.PtrTarget} (ttl {r.Ttl})";
            case DnsType.A: return $"A {r.Name} -> {new IPAddress(r.Data)} (ttl {r.Ttl})";
            case DnsType.SRV:
                {
                    int port = (r.Data[4] << 8) | r.Data[5];
                    // target name inside rdata may use compression pointers into the packet: decode relative to the full packet
                    string target;
                    try { int off = 6; target = DnsWire.ReadName(r.Data, ref off); }
                    catch (Exception) { target = "?"; }
                    return $"SRV {r.Name} -> {target}:{port} (ttl {r.Ttl})";
                }
            case DnsType.TXT:
                {
                    var sb = new StringBuilder();
                    int i = 0;
                    while (i < r.Data.Length)
                    {
                        int len = r.Data[i++];
                        if (len == 0 || i + len > r.Data.Length) break;
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(Encoding.UTF8.GetString(r.Data, i, len));
                        i += len;
                    }
                    return $"TXT {r.Name}: {sb}";
                }
            default: return $"type {r.Type} {r.Name} ({r.Data.Length} bytes, ttl {r.Ttl})";
        }
    }
}
