using ImageWriterII.Core.Raster;
using ImageWriterII.Core.Text;
using ImageWriterII.Ipp.Protocol;
using Microsoft.Extensions.Logging;

namespace ImageWriterII.Ipp.Server;

/// <summary>Per-request transport details the IPP layer needs to build URIs and receive documents.</summary>
public sealed class IppRequestContext
{
    /// <summary>Host header value as the client sent it (host or host:port).</summary>
    public required string Host { get; init; }
    public required string ResourcePath { get; init; }
    public string RemoteAddress { get; init; } = "";
    /// <summary>Stream positioned at the document data that follows the IPP message (Print-Job, Send-Document).</summary>
    public Stream? Document { get; init; }
    /// <summary>Spool file the document should be written to (already contains the request; offset = current position).</summary>
    public string? SpoolPath { get; init; }
    public long DocumentOffset { get; init; }
    public long DocumentLength { get; init; }
    /// <summary>Set by the printer when a job took ownership of the spool file.</summary>
    public bool SpoolConsumed { get; set; }
}

/// <summary>
/// The IPP Everywhere printer object: capabilities, job creation and status operations.
/// Attribute set modelled on CUPS ippeveprinter and PAPPL, which the Windows and Apple clients are known to accept.
/// </summary>
public sealed class IppPrinter
{
    public const string ResourcePath = "/ipp/print";

    private static readonly string[] DocumentFormats = ["image/pwg-raster", "image/urf", "application/octet-stream", "text/plain"];
    private static readonly string[] JobCreationAttributes =
    [
        "copies", "document-format", "document-name", "ipp-attribute-fidelity", "job-name", "job-priority",
        "media", "media-col", "multiple-document-handling", "orientation-requested", "print-color-mode",
        "print-content-optimize", "print-quality", "print-scaling", "printer-resolution"
    ];
    private static readonly IppOperation[] Operations =
    [
        IppOperation.PrintJob, IppOperation.ValidateJob, IppOperation.CreateJob, IppOperation.SendDocument,
        IppOperation.CancelJob, IppOperation.GetJobAttributes, IppOperation.GetJobs, IppOperation.GetPrinterAttributes,
        IppOperation.CancelMyJobs, IppOperation.CloseJob, IppOperation.IdentifyPrinter
    ];

    private readonly PrinterConfig _cfg;
    private readonly JobStore _jobs;
    private readonly PrintSpooler _spooler;
    private readonly PrinterStatus _status;
    private readonly ILogger _log;
    private readonly DateTimeOffset _configChanged = DateTimeOffset.Now;

    public IppPrinter(PrinterConfig config, JobStore jobs, PrintSpooler spooler, PrinterStatus status, ILogger<IppPrinter> logger)
    {
        _cfg = config;
        _jobs = jobs;
        _spooler = spooler;
        _status = status;
        _log = logger;
    }

    public JobStore Jobs => _jobs;

    // ------------------------------------------------------------------ dispatch

    public IppMessage Handle(IppMessage request, IppRequestContext ctx)
    {
        var response = new IppMessage
        {
            VersionMajor = request.VersionMajor,
            VersionMinor = request.VersionMinor,
            RequestId = request.RequestId
        };
        if (request.VersionMajor > 2) { response.VersionMajor = 2; response.VersionMinor = 0; }
        else if (request.VersionMajor == 2 && request.VersionMinor > 0) response.VersionMinor = 0;
        else if (request.VersionMajor < 1) { response.VersionMajor = 1; response.VersionMinor = 1; }

        var opGroup = response.AddGroup(IppTag.OperationAttributes);
        opGroup.Add("attributes-charset", IppValue.Charset("utf-8"));
        opGroup.Add("attributes-natural-language", IppValue.Language(RequestLanguage(request)));

        try
        {
            if (request.VersionMajor is < 1 or > 2)
                throw new IppException(IppStatus.ServerErrorVersionNotSupported, $"IPP/{request.VersionMajor}.{request.VersionMinor} not supported");

            var status = request.Operation switch
            {
                IppOperation.GetPrinterAttributes => GetPrinterAttributes(request, response, ctx),
                IppOperation.CupsGetDefault => GetPrinterAttributes(request, response, ctx),
                IppOperation.CupsGetPrinters => GetPrinterAttributes(request, response, ctx),
                IppOperation.ValidateJob => ValidateJob(request, response),
                IppOperation.PrintJob => PrintJob(request, response, ctx),
                IppOperation.CreateJob => CreateJob(request, response, ctx),
                IppOperation.SendDocument => SendDocument(request, response, ctx),
                IppOperation.CloseJob => CloseJob(request, response, ctx),
                IppOperation.GetJobs => GetJobs(request, response, ctx),
                IppOperation.GetJobAttributes => GetJobAttributes(request, response, ctx),
                IppOperation.CancelJob => CancelJob(request, response),
                IppOperation.CancelMyJobs => CancelMyJobs(request, response),
                IppOperation.IdentifyPrinter => IdentifyPrinter(request, response),
                _ => throw new IppException(IppStatus.ServerErrorOperationNotSupported, $"Operation 0x{request.OperationOrStatus:X4} not supported")
            };
            response.OperationOrStatus = (ushort)status;
        }
        catch (IppException ex)
        {
            _log.LogWarning("IPP {Operation} from {Remote}: {Status} {Message}", request.Operation, ctx.RemoteAddress, ex.Status, ex.Message);
            response.Groups.RemoveAll(g => g.Tag != IppTag.OperationAttributes);
            response.OperationOrStatus = (ushort)ex.Status;
            opGroup.Add("status-message", IppValue.Text(ex.Message));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "IPP {Operation} failed", request.Operation);
            response.Groups.RemoveAll(g => g.Tag != IppTag.OperationAttributes);
            response.OperationOrStatus = (ushort)IppStatus.ServerErrorInternalError;
            opGroup.Add("status-message", IppValue.Text(ex.Message));
        }
        return response;
    }

    private static string RequestLanguage(IppMessage request)
    {
        var lang = request.GetString(IppTag.OperationAttributes, "attributes-natural-language");
        return string.IsNullOrWhiteSpace(lang) ? "en" : lang;
    }

    private static string RequestingUser(IppMessage request)
    {
        var user = request.GetString(IppTag.OperationAttributes, "requesting-user-name");
        return string.IsNullOrWhiteSpace(user) ? "anonymous" : user;
    }

    // ------------------------------------------------------------------ URIs

    public string PrinterUri(IppRequestContext ctx) => $"ipp://{ctx.Host}{ResourcePath}";
    public string HttpBase(IppRequestContext ctx) => $"http://{ctx.Host}/";
    private string JobUri(IppRequestContext ctx, int id) => $"ipp://{ctx.Host}{ResourcePath}/job/{id}";

    // ------------------------------------------------------------------ Get-Printer-Attributes

    private IppStatus GetPrinterAttributes(IppMessage request, IppMessage response, IppRequestContext ctx)
    {
        var format = request.GetString(IppTag.OperationAttributes, "document-format");
        if (format is not null && !DocumentFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            throw new IppException(IppStatus.ClientErrorDocumentFormatNotSupported, $"Document format {format} not supported");

        var requested = RequestedAttributes(request);
        var group = response.AddGroup(IppTag.PrinterAttributes);
        foreach (var attr in BuildPrinterAttributes(ctx))
        {
            if (WantAttribute(requested, attr.Name)) group.Add(attr);
        }
        return IppStatus.SuccessfulOk;
    }

    private static HashSet<string>? RequestedAttributes(IppMessage request)
    {
        var attr = request.Find(IppTag.OperationAttributes, "requested-attributes");
        if (attr is null) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in attr.Values) set.Add(v.AsString());
        return set;
    }

    /// <summary>
    /// The Job Template attributes this printer emits (RFC 8011 section 5.2 and PWG 5100.x). Everything else
    /// BuildPrinterAttributes produces is Printer Description. A suffix rule cannot tell the two apart:
    /// "-supported" also ends operations-supported, ipp-versions-supported, document-format-supported,
    /// charset-supported, urf-supported and a dozen more that are Printer Description, and "-default" also
    /// ends document-format-default. Getting it wrong drops required attributes from a client that asks for
    /// the group by name (ipptool, the IPP Everywhere self-certification suite) rather than for "all".
    /// </summary>
    private static readonly HashSet<string> JobTemplateAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "copies-default", "copies-supported",
        "finishings-default", "finishings-supported",
        "job-priority-default", "job-priority-supported",
        "job-sheets-default", "job-sheets-supported",
        "media-default", "media-supported", "media-ready", "media-size-supported",
        "media-col-default", "media-col-supported", "media-col-ready", "media-col-database",
        "media-bottom-margin-supported", "media-left-margin-supported", "media-right-margin-supported",
        "media-top-margin-supported", "media-source-supported", "media-type-supported",
        "multiple-document-handling-default", "multiple-document-handling-supported",
        "orientation-requested-default", "orientation-requested-supported",
        "output-bin-default", "output-bin-supported",
        "print-color-mode-default", "print-color-mode-supported",
        "print-content-optimize-default", "print-content-optimize-supported",
        "print-quality-default", "print-quality-supported",
        "print-scaling-default", "print-scaling-supported",
        "printer-resolution-default", "printer-resolution-supported",
        "sides-default", "sides-supported",
    };

    private static bool WantAttribute(HashSet<string>? requested, string name)
    {
        // PWG 5100.7: media-col-database only when explicitly requested (it is large).
        if (name == "media-col-database") return requested is not null && requested.Contains(name);
        if (requested is null || requested.Contains("all")) return true;
        if (requested.Contains(name)) return true;
        bool isJobTemplate = JobTemplateAttributes.Contains(name);
        if (requested.Contains("job-template") && isJobTemplate) return true;
        if (requested.Contains("printer-description") && !isJobTemplate) return true;
        return false;
    }

    private IEnumerable<IppAttribute> BuildPrinterAttributes(IppRequestContext ctx)
    {
        var list = new List<IppAttribute>();
        void Add(string name, params IppValue[] values) => list.Add(new IppAttribute(name, values));
        void AddKeywords(string name, IEnumerable<string> values) => list.Add(new IppAttribute(name, values.Select(IppValue.Keyword)));

        var resolutions = _cfg.ResolutionList();
        var defaultRes = _cfg.DefaultResolutionValue();
        bool color = _cfg.ColorRibbon;
        string httpBase = HttpBase(ctx);
        var media = _cfg.MediaSupported.Select(PwgMedia.Parse).ToList();
        var mediaDefault = PwgMedia.Parse(_cfg.MediaDefault);
        var mediaMin = PwgMedia.Parse(_cfg.MediaMin);
        var mediaMax = PwgMedia.Parse(_cfg.MediaMax);
        int left = Hundredths(_cfg.MarginLeftInches), right = Hundredths(_cfg.MarginRightInches);
        int top = Hundredths(_cfg.MarginTopInches), bottom = Hundredths(_cfg.MarginBottomInches);
        var now = DateTimeOffset.Now;

        Add("charset-configured", IppValue.Charset("utf-8"));
        Add("charset-supported", IppValue.Charset("utf-8"), IppValue.Charset("us-ascii"));
        Add("color-supported", IppValue.Boolean(color));
        AddKeywords("compression-supported", ["none", "gzip", "deflate"]);
        Add("copies-default", IppValue.Integer(1));
        Add("copies-supported", IppValue.Range(1, 99));
        Add("document-format-default", IppValue.MimeType("application/octet-stream"));
        Add("document-format-preferred", IppValue.MimeType("image/pwg-raster"));
        list.Add(new IppAttribute("document-format-supported", DocumentFormats.Select(IppValue.MimeType)));
        Add("finishings-default", IppValue.Enum(3));
        Add("finishings-supported", IppValue.Enum(3));
        Add("generated-natural-language-supported", IppValue.Language("en"));
        Add("identify-actions-default", IppValue.Keyword("sound"));
        Add("identify-actions-supported", IppValue.Keyword("sound"));
        Add("ipp-features-supported", IppValue.Keyword("ipp-everywhere"));
        AddKeywords("ipp-versions-supported", ["1.0", "1.1", "2.0"]);
        AddKeywords("job-creation-attributes-supported", JobCreationAttributes);
        Add("job-ids-supported", IppValue.Boolean(true));
        Add("job-k-octets-supported", IppValue.Range(0, 2_000_000));
        Add("job-priority-default", IppValue.Integer(50));
        Add("job-priority-supported", IppValue.Integer(1));
        Add("job-sheets-default", IppValue.Name("none"));
        Add("job-sheets-supported", IppValue.Name("none"));
        Add("landscape-orientation-requested-preferred", IppValue.Enum(4));

        Add("media-bottom-margin-supported", IppValue.Integer(bottom));
        Add("media-left-margin-supported", IppValue.Integer(left));
        Add("media-right-margin-supported", IppValue.Integer(right));
        Add("media-top-margin-supported", IppValue.Integer(top));

        var db = new List<IppValue>();
        foreach (var m in media) db.Add(IppValue.Collection(MediaCol(m, left, right, top, bottom, includeSourceAndType: false)));
        db.Add(IppValue.Collection(MediaColRange(mediaMin, mediaMax, left, right, top, bottom)));
        list.Add(new IppAttribute("media-col-database", db));
        Add("media-col-default", IppValue.Collection(MediaCol(mediaDefault, left, right, top, bottom, includeSourceAndType: true)));
        Add("media-col-ready", IppValue.Collection(MediaCol(mediaDefault, left, right, top, bottom, includeSourceAndType: true)));
        AddKeywords("media-col-supported", ["media-bottom-margin", "media-left-margin", "media-right-margin", "media-size", "media-size-name", "media-source", "media-top-margin", "media-type"]);
        Add("media-default", IppValue.Keyword(mediaDefault.Name));
        Add("media-ready", IppValue.Keyword(mediaDefault.Name));
        var sizes = media.Select(m => IppValue.Collection(new IppCollection().Add("x-dimension", IppValue.Integer(m.WidthHundredthsMm)).Add("y-dimension", IppValue.Integer(m.HeightHundredthsMm)))).ToList();
        sizes.Add(IppValue.Collection(new IppCollection()
            .Add("x-dimension", IppValue.Range(mediaMin.WidthHundredthsMm, mediaMax.WidthHundredthsMm))
            .Add("y-dimension", IppValue.Range(mediaMin.HeightHundredthsMm, mediaMax.HeightHundredthsMm))));
        list.Add(new IppAttribute("media-size-supported", sizes));
        AddKeywords("media-source-supported", ["main"]);
        AddKeywords("media-supported", media.Select(m => m.Name).Concat([mediaMin.Name, mediaMax.Name]));
        AddKeywords("media-type-supported", ["stationery", "continuous"]);
        Add("multiple-document-handling-default", IppValue.Keyword("separate-documents-collated-copies"));
        AddKeywords("multiple-document-handling-supported", ["separate-documents-collated-copies", "separate-documents-uncollated-copies"]);
        Add("multiple-document-jobs-supported", IppValue.Boolean(false));
        Add("multiple-operation-time-out", IppValue.Integer(120));
        Add("multiple-operation-time-out-action", IppValue.Keyword("abort-job"));
        Add("natural-language-configured", IppValue.Language("en"));
        list.Add(new IppAttribute("operations-supported", Operations.Select(o => IppValue.Enum((int)o))));
        Add("orientation-requested-default", IppValue.Enum(3));
        list.Add(new IppAttribute("orientation-requested-supported", new[] { 3, 4, 5, 6 }.Select(IppValue.Enum)));
        Add("output-bin-default", IppValue.Keyword("face-up"));
        Add("output-bin-supported", IppValue.Keyword("face-up"));
        Add("pages-per-minute", IppValue.Integer(1));
        if (color) Add("pages-per-minute-color", IppValue.Integer(1));
        Add("pdl-override-supported", IppValue.Keyword("attempted"));
        Add("print-color-mode-default", IppValue.Keyword(color ? "auto" : "monochrome"));
        AddKeywords("print-color-mode-supported", color ? ["auto", "color", "monochrome"] : ["monochrome"]);
        Add("print-content-optimize-default", IppValue.Keyword("auto"));
        AddKeywords("print-content-optimize-supported", ["auto", "graphic", "photo", "text", "text-and-graphic"]);
        Add("print-quality-default", IppValue.Enum(4));
        list.Add(new IppAttribute("print-quality-supported", new[] { 3, 4, 5 }.Select(IppValue.Enum)));
        Add("print-scaling-default", IppValue.Keyword("auto"));
        AddKeywords("print-scaling-supported", ["auto", "auto-fit", "fill", "fit", "none"]);
        Add("printer-config-change-date-time", IppValue.DateTime(_configChanged));
        Add("printer-config-change-time", IppValue.Integer(_status.SecondsSinceStart(_configChanged)));
        Add("printer-current-time", IppValue.DateTime(now));
        Add("printer-device-id", IppValue.Text($"MFG:Apple;MDL:ImageWriter II;CMD:PWGRaster,URF;CLS:PRINTER;DES:{_cfg.MakeAndModel};"));
        Add("printer-dns-sd-name", IppValue.Name(_cfg.PrinterName));
        Add("printer-firmware-name", IppValue.Name("ImageWriterII-IPP"));
        Add("printer-firmware-string-version", IppValue.Text(typeof(IppPrinter).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"));
        Add("printer-geo-location", IppValue.Unknown());
        Add("printer-get-attributes-supported", IppValue.Keyword("document-format"));
        Add("printer-icons", IppValue.Uri(httpBase + "icon-48.png"), IppValue.Uri(httpBase + "icon-128.png"), IppValue.Uri(httpBase + "icon-512.png"));
        Add("printer-info", IppValue.Text(_cfg.MakeAndModel));
        Add("printer-is-accepting-jobs", IppValue.Boolean(true));
        Add("printer-kind", IppValue.Keyword("document"));
        Add("printer-location", IppValue.Text(_cfg.Location));
        Add("printer-make-and-model", IppValue.Text(_cfg.MakeAndModel));
        Add("printer-more-info", IppValue.Uri(httpBase));
        Add("printer-name", IppValue.Name(_cfg.PrinterName));
        Add("printer-organization", IppValue.Text(_cfg.Organization));
        Add("printer-organizational-unit", IppValue.Text(""));
        Add("printer-resolution-default", new IppValue(IppTag.Resolution, defaultRes));
        list.Add(new IppAttribute("printer-resolution-supported", resolutions.Select(r => new IppValue(IppTag.Resolution, r))));
        Add("printer-state", IppValue.Enum((int)_status.State));
        Add("printer-state-change-date-time", IppValue.DateTime(_status.StateChangedAt));
        Add("printer-state-change-time", IppValue.Integer(_status.SecondsSinceStart(_status.StateChangedAt)));
        Add("printer-state-message", IppValue.Text(_status.Message));
        AddKeywords("printer-state-reasons", _status.Reasons);
        if (color)
        {
            list.Add(new IppAttribute("printer-supply",
                IppValue.Octets("type=ink;maxcapacity=-2;level=-2;class=supplyThatIsConsumed;unit=percent;colorantname=black;"u8.ToArray()),
                IppValue.Octets("type=ink;maxcapacity=-2;level=-2;class=supplyThatIsConsumed;unit=percent;colorantname=yellow;"u8.ToArray()),
                IppValue.Octets("type=ink;maxcapacity=-2;level=-2;class=supplyThatIsConsumed;unit=percent;colorantname=magenta;"u8.ToArray()),
                IppValue.Octets("type=ink;maxcapacity=-2;level=-2;class=supplyThatIsConsumed;unit=percent;colorantname=cyan;"u8.ToArray())));
            Add("printer-supply-description", IppValue.Text("Four-colour ribbon (black)"), IppValue.Text("Four-colour ribbon (yellow)"), IppValue.Text("Four-colour ribbon (magenta)"), IppValue.Text("Four-colour ribbon (cyan)"));
        }
        else
        {
            Add("printer-supply", IppValue.Octets("type=ink;maxcapacity=-2;level=-2;class=supplyThatIsConsumed;unit=percent;colorantname=black;"u8.ToArray()));
            Add("printer-supply-description", IppValue.Text("Black fabric ribbon"));
        }
        Add("printer-supply-info-uri", IppValue.Uri(httpBase));
        Add("printer-up-time", IppValue.Integer(_status.UpTimeSeconds));
        Add("printer-uri-supported", IppValue.Uri(PrinterUri(ctx)));
        Add("printer-uuid", IppValue.Uri("urn:uuid:" + _cfg.Uuid));
        list.Add(new IppAttribute("pwg-raster-document-resolution-supported", resolutions.Select(r => new IppValue(IppTag.Resolution, r))));
        Add("pwg-raster-document-sheet-back", IppValue.Keyword("normal"));
        AddKeywords("pwg-raster-document-type-supported", color ? ["black_1", "sgray_8", "srgb_8"] : ["black_1", "sgray_8"]);
        Add("queued-job-count", IppValue.Integer(_jobs.QueuedCount));
        Add("sides-default", IppValue.Keyword("one-sided"));
        Add("sides-supported", IppValue.Keyword("one-sided"));
        Add("uri-authentication-supported", IppValue.Keyword("none"));
        Add("uri-security-supported", IppValue.Keyword("none"));
        AddKeywords("urf-supported", UrfSupported(resolutions, color));
        AddKeywords("which-jobs-supported", ["completed", "not-completed", "all"]);
        return list;
    }

    public static IEnumerable<string> UrfSupported(IEnumerable<IppResolution> resolutions, bool color)
    {
        // Apple raster is square-resolution only; advertise the square ones.
        var rs = resolutions.Where(r => r.X == r.Y).Select(r => r.X).Distinct().OrderBy(x => x).ToList();
        if (rs.Count == 0) rs.Add(144);
        var list = new List<string> { "V1.4", "W8", "CP1", "IS1", "MT1-2", "OB9", "PQ3-4-5", "RS" + string.Join("-", rs) };
        if (color) list.Insert(2, "SRGB24");
        return list;
    }

    private static int Hundredths(double inches) => (int)Math.Round(inches * 2540);

    private static IppCollection MediaCol(PwgMedia m, int left, int right, int top, int bottom, bool includeSourceAndType)
    {
        var col = new IppCollection()
            .Add("media-size", IppValue.Collection(new IppCollection().Add("x-dimension", IppValue.Integer(m.WidthHundredthsMm)).Add("y-dimension", IppValue.Integer(m.HeightHundredthsMm))))
            .Add("media-size-name", IppValue.Keyword(m.Name))
            .Add("media-bottom-margin", IppValue.Integer(bottom))
            .Add("media-left-margin", IppValue.Integer(left))
            .Add("media-right-margin", IppValue.Integer(right))
            .Add("media-top-margin", IppValue.Integer(top));
        if (includeSourceAndType)
        {
            col.Add("media-source", IppValue.Keyword("main"));
            col.Add("media-type", IppValue.Keyword("stationery"));
        }
        return col;
    }

    private static IppCollection MediaColRange(PwgMedia min, PwgMedia max, int left, int right, int top, int bottom) =>
        new IppCollection()
            .Add("media-size", IppValue.Collection(new IppCollection()
                .Add("x-dimension", IppValue.Range(min.WidthHundredthsMm, max.WidthHundredthsMm))
                .Add("y-dimension", IppValue.Range(min.HeightHundredthsMm, max.HeightHundredthsMm))))
            .Add("media-bottom-margin", IppValue.Integer(bottom))
            .Add("media-left-margin", IppValue.Integer(left))
            .Add("media-right-margin", IppValue.Integer(right))
            .Add("media-top-margin", IppValue.Integer(top));

    // ------------------------------------------------------------------ job creation

    private IppStatus ValidateJob(IppMessage request, IppMessage response)
    {
        var unsupported = ValidateJobAttributes(request, out _);
        if (unsupported.Count > 0)
        {
            var g = response.AddGroup(IppTag.UnsupportedAttributes);
            foreach (var a in unsupported) g.Add(a);
            return IppStatus.SuccessfulOkIgnoredOrSubstitutedAttributes;
        }
        return IppStatus.SuccessfulOk;
    }

    /// <summary>Checks operation and job-template attributes; returns the ones we ignore (reported as unsupported).</summary>
    private List<IppAttribute> ValidateJobAttributes(IppMessage request, out string format)
    {
        var unsupported = new List<IppAttribute>();
        format = request.GetString(IppTag.OperationAttributes, "document-format") ?? "application/octet-stream";
        if (!DocumentFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            throw new IppException(IppStatus.ClientErrorDocumentFormatNotSupported, $"Document format {format} not supported");
        var compression = request.GetString(IppTag.OperationAttributes, "compression") ?? "none";
        if (compression is not ("none" or "gzip" or "deflate"))
            throw new IppException(IppStatus.ClientErrorCompressionNotSupported, $"Compression {compression} not supported");

        var template = request.Group(IppTag.JobAttributes);
        if (template is null) return unsupported;
        bool fidelity = request.GetBool(IppTag.OperationAttributes, "ipp-attribute-fidelity") ?? false;
        foreach (var a in template.Attributes)
        {
            bool ok = a.Name switch
            {
                "copies" => a.First.Data is int c && c is >= 1 and <= 99,
                "media" => IsSupportedMedia(a.First.AsString()),
                "media-col" => true,
                "printer-resolution" => a.First.Data is IppResolution r && _cfg.ResolutionList().Contains(r),
                "print-quality" => a.First.Data is int q && q is >= 3 and <= 5,
                "print-color-mode" => a.First.AsString() is "monochrome" or "auto" or "color" or "auto-monochrome" or "process-monochrome" or "bi-level",
                "orientation-requested" => a.First.Data is int o && o is >= 3 and <= 6,
                "sides" => a.First.AsString() == "one-sided",
                "multiple-document-handling" => a.First.AsString().StartsWith("separate-documents", StringComparison.Ordinal),
                "output-bin" => a.First.AsString() == "face-up",
                "finishings" => a.First.Data is int f && f == 3,
                "print-scaling" or "print-content-optimize" or "job-name" or "job-priority" or "document-name" or "job-hold-until" or "page-ranges" or "print-rendering-intent" or "number-up" or "job-sheets" => true,
                _ => false
            };
            if (!ok)
            {
                if (fidelity) throw new IppException(IppStatus.ClientErrorAttributesOrValuesNotSupported, $"Attribute {a.Name} not supported");
                unsupported.Add(new IppAttribute(a.Name, IppValue.Unsupported()));
            }
        }
        return unsupported;
    }

    private bool IsSupportedMedia(string name)
    {
        if (_cfg.MediaSupported.Contains(name)) return true;
        if (!PwgMedia.TryParse(name, out var m)) return false;
        var min = PwgMedia.Parse(_cfg.MediaMin);
        var max = PwgMedia.Parse(_cfg.MediaMax);
        return m.WidthHundredthsMm >= min.WidthHundredthsMm && m.WidthHundredthsMm <= max.WidthHundredthsMm
            && m.HeightHundredthsMm >= min.HeightHundredthsMm && m.HeightHundredthsMm <= max.HeightHundredthsMm;
    }

    private PrintJob NewJob(IppMessage request, string format)
    {
        var job = new PrintJob
        {
            Id = _jobs.NextId(),
            Name = request.GetString(IppTag.OperationAttributes, "job-name") ?? "Untitled",
            UserName = RequestingUser(request),
            DocumentName = request.GetString(IppTag.OperationAttributes, "document-name") ?? "",
            Format = format,
            Compression = request.GetString(IppTag.OperationAttributes, "compression") ?? "none"
        };
        var template = request.Group(IppTag.JobAttributes);
        if (template is not null)
        {
            foreach (var a in template.Attributes)
            {
                job.TemplateAttributes.Add(a);
                switch (a.Name)
                {
                    case "copies": if (a.First.Data is int c) job.Copies = Math.Clamp(c, 1, 99); break;
                    case "media": job.Media = a.First.AsString(); break;
                    case "media-col":
                        if (a.First.Data is IppCollection col && col.Find("media-size-name")?.First.AsString() is { } sizeName) job.Media = sizeName;
                        break;
                    case "printer-resolution": if (a.First.Data is IppResolution r) job.Resolution = r; break;
                    case "print-quality": if (a.First.Data is int q) job.PrintQuality = q; break;
                    case "print-color-mode": job.ColorMode = a.First.AsString(); break;
                    case "multiple-document-handling": job.Collate = a.First.AsString() != "separate-documents-uncollated-copies"; break;
                }
            }
        }
        return job;
    }

    private IppStatus PrintJob(IppMessage request, IppMessage response, IppRequestContext ctx)
    {
        var unsupported = ValidateJobAttributes(request, out var format);
        var job = NewJob(request, format);
        AttachDocument(job, ctx);
        job.DocumentsComplete = true;
        _jobs.Add(job);
        _spooler.Enqueue(job);
        AddJobStatus(response, job, ctx);
        return ReportUnsupported(response, unsupported);
    }

    private IppStatus CreateJob(IppMessage request, IppMessage response, IppRequestContext ctx)
    {
        var unsupported = ValidateJobAttributes(request, out var format);
        var job = NewJob(request, format);
        job.SetState(IppJobState.PendingHeld, "job-incoming", "job-data-insufficient");
        _jobs.Add(job);
        AddJobStatus(response, job, ctx);
        return ReportUnsupported(response, unsupported);
    }

    private IppStatus SendDocument(IppMessage request, IppMessage response, IppRequestContext ctx)
    {
        var job = FindJob(request);
        // A job that has already been cancelled or aborted must not come back to life: Enqueue resets the
        // state to Pending, so without this a Send-Document racing a Cancel-Job would print after all.
        if (job.IsFinished)
            throw new IppException(IppStatus.ClientErrorNotPossible, $"Job {job.Id} is already {job.State}");

        bool last = request.GetBool(IppTag.OperationAttributes, "last-document") ?? true;
        var format = request.GetString(IppTag.OperationAttributes, "document-format");
        if (format is not null)
        {
            if (!DocumentFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
                throw new IppException(IppStatus.ClientErrorDocumentFormatNotSupported, $"Document format {format} not supported");
            job.Format = format;
        }
        var compression = request.GetString(IppTag.OperationAttributes, "compression");
        if (compression is not null) job.Compression = compression;
        var docName = request.GetString(IppTag.OperationAttributes, "document-name");
        if (docName is not null) job.DocumentName = docName;

        if (job.DocumentsComplete)
            throw new IppException(IppStatus.ClientErrorNotPossible, "Job already has a document");

        if (ctx.DocumentLength > 0)
        {
            if (job.SpoolPath is not null)
                throw new IppException(IppStatus.ServerErrorMultipleDocumentJobsNotSupported, "Only one document per job is supported");
            AttachDocument(job, ctx);
        }
        if (last)
        {
            job.DocumentsComplete = true;
            if (job.SpoolPath is null)
            {
                job.SetState(IppJobState.Aborted, "job-aborted-by-system", "job-data-insufficient");
            }
            else
            {
                _spooler.Enqueue(job);
            }
        }
        AddJobStatus(response, job, ctx);
        return IppStatus.SuccessfulOk;
    }

    private IppStatus CloseJob(IppMessage request, IppMessage response, IppRequestContext ctx)
    {
        var job = FindJob(request);
        if (!job.DocumentsComplete)
        {
            job.DocumentsComplete = true;
            if (job.SpoolPath is not null) _spooler.Enqueue(job);
            else job.SetState(IppJobState.Aborted, "job-aborted-by-system", "job-data-insufficient");
        }
        AddJobStatus(response, job, ctx);
        return IppStatus.SuccessfulOk;
    }

    private void AttachDocument(PrintJob job, IppRequestContext ctx)
    {
        if (ctx.SpoolPath is null)
            throw new IppException(IppStatus.ClientErrorBadRequest, "No document data");
        job.SpoolPath = ctx.SpoolPath;
        job.DocumentOffset = ctx.DocumentOffset;
        job.DocumentBytes = ctx.DocumentLength;
        ctx.SpoolConsumed = true;
        job.Kind = DetectKind(job);
        _log.LogInformation("Job {Id} '{Name}' from {User}: {Format} -> {Kind}, {Bytes} bytes, copies {Copies}, media {Media}, {Res}, quality {Q}, {Color}",
            job.Id, job.Name, job.UserName, job.Format, job.Kind, job.DocumentBytes, job.Copies, job.Media, job.Resolution?.ToString() ?? "default-res", job.PrintQuality, job.ColorMode);
    }

    private static JobDocumentKind DetectKind(PrintJob job)
    {
        if (job.SpoolPath is null || job.DocumentBytes == 0) return JobDocumentKind.None;
        if (job.Compression != "none")
        {
            return job.Format.ToLowerInvariant() switch
            {
                "image/pwg-raster" => JobDocumentKind.PwgRaster,
                "image/urf" => JobDocumentKind.AppleRaster,
                "text/plain" => JobDocumentKind.Text,
                _ => JobDocumentKind.Raw
            };
        }
        Span<byte> head = stackalloc byte[8];
        int n;
        using (var f = File.OpenRead(job.SpoolPath))
        {
            f.Position = job.DocumentOffset;
            n = f.Read(head);
        }
        if (n >= 4 && (head[..4].SequenceEqual("RaS2"u8) || head[..4].SequenceEqual("2SaR"u8) || head[..4].SequenceEqual("RaS3"u8) || head[..4].SequenceEqual("3SaR"u8)))
            return JobDocumentKind.PwgRaster;
        if (n >= 7 && head[..7].SequenceEqual("UNIRAST"u8))
            return JobDocumentKind.AppleRaster;
        if (job.Format.Equals("text/plain", StringComparison.OrdinalIgnoreCase)) return JobDocumentKind.Text;
        // Sniff the start of the document: raw printer data has escapes, text does not.
        var sample = new byte[Math.Min(4096, job.DocumentBytes)];
        using (var f = File.OpenRead(job.SpoolPath))
        {
            f.Position = job.DocumentOffset;
            int total = 0;
            while (total < sample.Length) { int r = f.Read(sample, total, sample.Length - total); if (r <= 0) break; total += r; }
        }
        return TextJobEncoder.LooksLikeRawPrinterData(sample) ? JobDocumentKind.Raw : JobDocumentKind.Text;
    }

    private static IppStatus ReportUnsupported(IppMessage response, List<IppAttribute> unsupported)
    {
        if (unsupported.Count == 0) return IppStatus.SuccessfulOk;
        var g = response.AddGroup(IppTag.UnsupportedAttributes);
        foreach (var a in unsupported) g.Add(a);
        return IppStatus.SuccessfulOkIgnoredOrSubstitutedAttributes;
    }

    // ------------------------------------------------------------------ job status

    private PrintJob FindJob(IppMessage request)
    {
        int? id = request.GetInt(IppTag.OperationAttributes, "job-id");
        if (id is null)
        {
            var uri = request.GetString(IppTag.OperationAttributes, "job-uri");
            if (uri is not null)
            {
                int slash = uri.LastIndexOf('/');
                if (slash >= 0 && int.TryParse(uri[(slash + 1)..], out var parsed)) id = parsed;
            }
        }
        if (id is null) throw new IppException(IppStatus.ClientErrorBadRequest, "Missing job-id");
        return _jobs.Get(id.Value) ?? throw new IppException(IppStatus.ClientErrorNotFound, $"Job {id} not found");
    }

    private void AddJobStatus(IppMessage response, PrintJob job, IppRequestContext ctx, HashSet<string>? requested = null)
    {
        var g = response.AddGroup(IppTag.JobAttributes);
        foreach (var a in JobAttributes(job, ctx))
        {
            if (requested is null || requested.Contains("all") || requested.Contains(a.Name) || (requested.Contains("job-description") && !job.TemplateAttributes.Contains(a)) || (requested.Contains("job-template") && job.TemplateAttributes.Contains(a)))
                g.Add(a);
        }
    }

    private IEnumerable<IppAttribute> JobAttributes(PrintJob job, IppRequestContext ctx)
    {
        lock (job.Sync)
        {
            yield return new IppAttribute("job-id", IppValue.Integer(job.Id));
            yield return new IppAttribute("job-uri", IppValue.Uri(JobUri(ctx, job.Id)));
            yield return new IppAttribute("job-printer-uri", IppValue.Uri(PrinterUri(ctx)));
            yield return new IppAttribute("job-state", IppValue.Enum((int)job.State));
            yield return new IppAttribute("job-state-reasons", job.StateReasons.Select(IppValue.Keyword));
            yield return new IppAttribute("job-state-message", IppValue.Text(job.StateMessage.Length > 0 ? job.StateMessage : JobStateText(job)));
            yield return new IppAttribute("job-name", IppValue.Name(job.Name));
            yield return new IppAttribute("job-originating-user-name", IppValue.Name(job.UserName));
            yield return new IppAttribute("document-format", IppValue.MimeType(job.Format));
            if (job.DocumentName.Length > 0) yield return new IppAttribute("document-name", IppValue.Name(job.DocumentName));
            yield return new IppAttribute("job-k-octets", IppValue.Integer((int)Math.Min(int.MaxValue, (job.DocumentBytes + 1023) / 1024)));
            yield return new IppAttribute("job-impressions", job.Impressions > 0 ? IppValue.Integer(job.Impressions) : IppValue.Unknown());
            yield return new IppAttribute("job-impressions-completed", IppValue.Integer(job.ImpressionsCompleted));
            yield return new IppAttribute("job-media-sheets-completed", IppValue.Integer(job.ImpressionsCompleted));
            yield return new IppAttribute("job-printer-up-time", IppValue.Integer(_status.UpTimeSeconds));
            yield return new IppAttribute("time-at-creation", IppValue.Integer(_status.SecondsSinceStart(job.CreatedAt)));
            yield return new IppAttribute("date-time-at-creation", IppValue.DateTime(job.CreatedAt));
            yield return new IppAttribute("time-at-processing", job.ProcessingAt is { } p ? IppValue.Integer(_status.SecondsSinceStart(p)) : IppValue.NoValue());
            yield return new IppAttribute("date-time-at-processing", job.ProcessingAt is { } p2 ? IppValue.DateTime(p2) : IppValue.NoValue());
            yield return new IppAttribute("time-at-completed", job.CompletedAt is { } c ? IppValue.Integer(_status.SecondsSinceStart(c)) : IppValue.NoValue());
            yield return new IppAttribute("date-time-at-completed", job.CompletedAt is { } c2 ? IppValue.DateTime(c2) : IppValue.NoValue());
            yield return new IppAttribute("copies", IppValue.Integer(job.Copies));
            if (job.Media.Length > 0) yield return new IppAttribute("media", IppValue.Keyword(job.Media));
            if (job.Resolution is { } r) yield return new IppAttribute("printer-resolution", new IppValue(IppTag.Resolution, r));
            if (job.PrintQuality > 0) yield return new IppAttribute("print-quality", IppValue.Enum(job.PrintQuality));
            if (job.ColorMode.Length > 0) yield return new IppAttribute("print-color-mode", IppValue.Keyword(job.ColorMode));
        }
    }

    private static string JobStateText(PrintJob job) => job.State switch
    {
        IppJobState.Pending => "Waiting to print",
        IppJobState.PendingHeld => "Waiting for document data",
        IppJobState.Processing => job.BandsTotal > 0 ? $"Printing page {job.ImpressionsCompleted + 1} ({job.BandsDone * 100 / Math.Max(1, job.BandsTotal)}%)" : "Printing",
        IppJobState.Completed => "Printed",
        IppJobState.Canceled => "Cancelled",
        IppJobState.Aborted => "Aborted",
        _ => ""
    };

    private IppStatus GetJobs(IppMessage request, IppMessage response, IppRequestContext ctx)
    {
        var which = request.GetString(IppTag.OperationAttributes, "which-jobs") ?? "not-completed";
        bool myJobs = request.GetBool(IppTag.OperationAttributes, "my-jobs") ?? false;
        int limit = request.GetInt(IppTag.OperationAttributes, "limit") ?? int.MaxValue;
        var requested = RequestedAttributes(request) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "job-id", "job-uri" };
        string user = RequestingUser(request);

        IEnumerable<PrintJob> jobs = which switch
        {
            "completed" => _jobs.Completed(),
            "all" => _jobs.All(),
            _ => _jobs.NotCompleted()
        };
        if (myJobs) jobs = jobs.Where(j => j.UserName == user);
        foreach (var job in jobs.Take(limit)) AddJobStatus(response, job, ctx, requested);
        return IppStatus.SuccessfulOk;
    }

    private IppStatus GetJobAttributes(IppMessage request, IppMessage response, IppRequestContext ctx)
    {
        var job = FindJob(request);
        AddJobStatus(response, job, ctx, RequestedAttributes(request));
        return IppStatus.SuccessfulOk;
    }

    private IppStatus CancelJob(IppMessage request, IppMessage response)
    {
        var job = FindJob(request);
        Cancel(job);
        return IppStatus.SuccessfulOk;
    }

    private IppStatus CancelMyJobs(IppMessage request, IppMessage response)
    {
        string user = RequestingUser(request);
        foreach (var job in _jobs.NotCompleted().Where(j => j.UserName == user).ToList()) Cancel(job);
        return IppStatus.SuccessfulOk;
    }

    public void Cancel(PrintJob job)
    {
        lock (job.Sync)
        {
            if (job.IsFinished) throw new IppException(IppStatus.ClientErrorNotPossible, $"Job {job.Id} is already {job.State}");
            if (job.State == IppJobState.Processing)
            {
                job.StateReasons.Clear();
                job.StateReasons.Add("processing-to-stop-point");
                job.Cancellation.Cancel();
            }
            else
            {
                job.SetState(IppJobState.Canceled, "job-canceled-by-user");
            }
        }
        _log.LogInformation("Cancel requested for {Job}", job);
    }

    private IppStatus IdentifyPrinter(IppMessage request, IppMessage response)
    {
        _spooler.Identify();
        return IppStatus.SuccessfulOk;
    }
}
