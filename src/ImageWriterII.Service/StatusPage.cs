using System.Net;
using System.Text;
using ImageWriterII.Ipp.Server;

namespace ImageWriterII.Service;

/// <summary>The small web page behind printer-more-info / adminurl.</summary>
public static class StatusPage
{
    public static string Render(PrinterConfig cfg, PrinterStatus status, JobStore jobs, PrintSpooler spooler, string host)
    {
        var sb = new StringBuilder();
        string E(string s) => WebUtility.HtmlEncode(s);
        sb.Append("<!doctype html><html><head><meta charset='utf-8'><meta http-equiv='refresh' content='5'>");
        sb.Append("<title>").Append(E(cfg.PrinterName)).Append("</title>");
        sb.Append("<style>body{font-family:system-ui,Segoe UI,sans-serif;margin:2em;max-width:60em;color:#222;background:#f7f5f0}");
        sb.Append("h1{margin:0 0 .2em}table{border-collapse:collapse;width:100%}td,th{text-align:left;padding:.3em .6em;border-bottom:1px solid #ddd}");
        sb.Append(".ok{color:#1a7f37}.warn{color:#b35900}.bad{color:#b00020}code{background:#eee;padding:0 .3em}.card{background:#fff;border:1px solid #e3ded4;border-radius:8px;padding:1em 1.4em;margin:1em 0}</style></head><body>");
        sb.Append("<h1><img src='/icon-48.png' alt='' style='vertical-align:middle;margin-right:.4em'>").Append(E(cfg.PrinterName)).Append("</h1>");
        sb.Append("<p>").Append(E(cfg.MakeAndModel)).Append(" &middot; IPP Everywhere service</p>");

        string cls = status.State switch { ImageWriterII.Ipp.Protocol.IppPrinterState.Idle => "ok", ImageWriterII.Ipp.Protocol.IppPrinterState.Processing => "warn", _ => "bad" };
        sb.Append("<div class='card'><h2>Printer</h2><table>");
        sb.Append($"<tr><th>State</th><td class='{cls}'>{status.State} &mdash; {E(status.Message)} ({E(string.Join(", ", status.Reasons))})</td></tr>");
        sb.Append($"<tr><th>Port</th><td>{E(status.PortDescription)} {(status.PortOpen ? "<span class='ok'>open</span>" : "<span>closed</span>")}</td></tr>");
        sb.Append($"<tr><th>Identity</th><td>{(status.Identity is null ? "not queried / no answer" : E(status.Identity.ToString()))}</td></tr>");
        sb.Append($"<tr><th>Ribbon</th><td>{(cfg.ColorRibbon ? "four-colour" : "black")}</td></tr>");
        sb.Append($"<tr><th>Resolutions</th><td>{E(string.Join(", ", cfg.ResolutionList()))} (default {E(cfg.DefaultResolution)})</td></tr>");
        sb.Append($"<tr><th>IPP URL</th><td><code>ipp://{E(host)}/ipp/print</code> &nbsp; (Windows: <code>http://{E(host)}/ipp/print</code>)</td></tr>");
        if (cfg.RawPortEnabled) sb.Append($"<tr><th>Raw port</th><td>TCP {cfg.RawPort}</td></tr>");
        sb.Append($"<tr><th>Uptime</th><td>{TimeSpan.FromSeconds(status.UpTimeSeconds):d\\.hh\\:mm\\:ss}, {status.BytesSentTotal:N0} bytes sent to the printer</td></tr>");
        sb.Append("</table></div>");

        sb.Append("<div class='card'><h2>Jobs</h2><table><tr><th>#</th><th>Name</th><th>User</th><th>Format</th><th>State</th><th>Pages</th><th>Created</th><th></th></tr>");
        var current = spooler.CurrentJob;
        foreach (var j in jobs.All().OrderByDescending(j => j.Id).Take(30))
        {
            string state = j.State.ToString();
            if (j == current && j.BandsTotal > 0) state += $" ({j.BandsDone * 100 / Math.Max(1, j.BandsTotal)}%)";
            string action = j.IsFinished ? "" : $"<form method='post' action='/api/jobs/{j.Id}/cancel' style='margin:0'><button>Cancel</button></form>";
            sb.Append($"<tr><td>{j.Id}</td><td>{E(j.Name)}</td><td>{E(j.UserName)}</td><td>{E(j.Format)} / {j.Kind}</td><td>{E(state)}</td><td>{j.ImpressionsCompleted}</td><td>{j.CreatedAt:HH:mm:ss}</td><td>{action}</td></tr>");
        }
        sb.Append("</table></div>");

        sb.Append("<div class='card'><h2>Setup</h2><ol>");
        sb.Append("<li>Windows 11: Settings &rarr; Bluetooth &amp; devices &rarr; Printers &amp; scanners &rarr; Add device. The printer should appear by name. If not: <em>Add manually</em> &rarr; <em>Select a shared printer by name</em> &rarr; <code>http://").Append(E(host)).Append("/ipp/print</code>.</li>");
        sb.Append("<li>PowerShell: <code>Add-Printer -Name \"ImageWriter II\" -IppURL http://").Append(E(host)).Append("/ipp/print</code></li>");
        sb.Append("<li>macOS / iOS / Linux: the printer is announced with Bonjour (AirPrint / IPP Everywhere).</li>");
        sb.Append("</ol></div>");
        sb.Append("<p><a href='/api/status'>JSON status</a></p></body></html>");
        return sb.ToString();
    }
}
