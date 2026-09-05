using System.Buffers;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net.Http.Headers;
using ImageWriterII.Core.Encoder;
using ImageWriterII.Core.Ports;
using ImageWriterII.Core.Printer;
using ImageWriterII.Core.Raster;
using ImageWriterII.Core.Simulation;
using ImageWriterII.Core.Text;
using ImageWriterII.Ipp.Protocol;

namespace ImageWriterII.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Run(string[] argv)
    {
        var args = new Args(argv);
        if (args.Positional.Count == 0 || args.Has("help") || args.Has("h"))
        {
            Usage();
            return args.Positional.Count == 0 ? 1 : 0;
        }
        string cmd = args.Positional[0].ToLowerInvariant();
        switch (cmd)
        {
            case "identify": return Identify(args);
            case "text": return Text(args);
            case "raw": return Raw(args);
            case "image": return Image(args);
            case "testpage": return TestPage(args);
            case "pwg": return Pwg(args);
            case "make-pwg": return MakePwg(args);
            case "render": return Render(args);
            case "ipp": return Ipp(args);
            case "mdns":
                return MdnsBrowse.Run(args.Positional.Count > 1 ? args.Positional[1] : "_ipp._tcp",
                    TimeSpan.FromSeconds(double.Parse(args.Get("wait") ?? "4", CultureInfo.InvariantCulture)));
            case "ports":
                foreach (var p in SerialPrinterPort.AvailablePorts()) Console.WriteLine(p);
                return 0;
            case "status": return PortStatus(args);
            default:
                Console.Error.WriteLine($"unknown command '{cmd}'");
                Usage();
                return 1;
        }
    }

    private static void Usage()
    {
        Console.WriteLine("""
            iwprint - Apple ImageWriter II command-line tool

            usage: iwprint <command> [options]

            commands
              ports                              list serial ports
              status [--port COM1]               show CTS/DSR/DCD and which handshake setting to use
              identify                           ask the printer for its ID string (ESC ?)
              text <file>  [--nlq|--draft] [--elite|--condensed] [--8lpi] [--raw]   print plain text with printer fonts
              raw <file>                         send a file to the printer byte for byte
              image <file> [--dpi 144x144] [--color] [--width <in>] [--gamma 1.8] [--dither fs|ordered|threshold]
                                                 print a PNG/JPEG/BMP/GIF scaled onto the page
              testpage     [--dpi 144x144] [--color] [--text "note"]   print an alignment / quality test page
              pwg <file>   [--color]             print a PWG raster (.pwg) or Apple raster (.urf) file
              make-pwg <image> <out.pwg> [--dpi 144x144] [--gray|--rgb|--bilevel] [--media na_letter_8.5x11in]
                                                 build a PWG raster from an image (to feed the IPP service)
              render <file.iw> <out.png> [--dpi 144] [--dot 0.0125] [--page 8.5x11]
                                                 simulate an ImageWriter escape stream and save it as an image
              ipp attrs <url>                    dump Get-Printer-Attributes from an IPP printer
              ipp jobs <url>                     list jobs
              ipp print <url> <file> [--format image/pwg-raster] [--name job] [--copies n] [--res 144x144] [--color]
                                                 send a document with Print-Job
              mdns [_ipp._tcp] [--wait 4]        browse the LAN for DNS-SD printers (checks the service's announcements)

            output options (text/raw/image/testpage/pwg)
              --port COM1        serial port (default COM1)     --baud 9600
              --handshake auto|rts|dsr|dcd|xon|both|none   (default auto: uses whichever of CTS/DSR/DCD the printer's DTR arrives on)
              --wait             keep waiting when the printer is not ready instead of giving up
              --out <file.iw>    write the escape stream to a file instead of the printer
              --left 0.25        inches from paper edge to dot column 0     --bidir   bidirectional graphics
            """);
    }

    // ------------------------------------------------------------------ helpers

    private static IPrinterPort OpenOutput(Args a)
    {
        var outFile = a.Get("out");
        if (outFile is not null)
            return new StreamPrinterPort(File.Create(outFile), outFile);
        var settings = new SerialPortSettings
        {
            PortName = a.Get("port") ?? "COM1",
            BaudRate = int.Parse(a.Get("baud") ?? "9600", CultureInfo.InvariantCulture),
            Handshake = (a.Get("handshake") ?? "auto").ToLowerInvariant() switch
            {
                "rts" or "cts" => Iw2Handshake.RequestToSend,
                "dsr" => Iw2Handshake.DataSetReady,
                "dcd" => Iw2Handshake.DataCarrierDetect,
                "xon" or "xonxoff" => Iw2Handshake.XOnXOff,
                "both" => Iw2Handshake.RequestToSendXOnXOff,
                "none" => Iw2Handshake.None,
                _ => Iw2Handshake.Auto
            },
            MaxBytesPerSecond = int.Parse(a.Get("pace") ?? "0", CultureInfo.InvariantCulture)
        };
        var port = new SerialPrinterPort(settings);
        port.Open();
        Console.Error.WriteLine($"opened {port.Description}: {port.LineStatus}");
        if (!port.IsReady)
        {
            Console.Error.WriteLine($"printer not ready: {port.Recommendation()}");
            if (!a.Has("wait")) { port.Dispose(); throw new InvalidOperationException("printer not ready (use --wait to keep waiting for it)"); }
            Console.Error.WriteLine("waiting for the printer...");
            while (!port.IsReady) Thread.Sleep(500);
        }
        return port;
    }

    private static Iw2EncoderOptions EncoderOptions(Args a, bool color) => new()
    {
        Bidirectional = a.Has("bidir"),
        LeftEdgeOffsetInches = double.Parse(a.Get("left") ?? "0.25", CultureInfo.InvariantCulture),
        ColorRibbon = color,
        ColorStrategy = a.Has("per-page") ? ColorStrategy.PerPage : ColorStrategy.PerBand,
        UseHeadPositioning = !a.Has("no-skip")
    };

    private static HalftoneOptions HalftoneOptions(Args a) => new()
    {
        Gamma = double.Parse(a.Get("gamma") ?? "1.8", CultureInfo.InvariantCulture),
        Mode = (a.Get("dither") ?? "fs").ToLowerInvariant() switch
        {
            "ordered" or "bayer" => DitherMode.Ordered,
            "threshold" or "none" => DitherMode.Threshold,
            _ => DitherMode.FloydSteinberg
        }
    };

    private static IppResolution ParseRes(string? s, int defX = 144, int defY = 144)
    {
        if (s is null) return new IppResolution(defX, defY);
        var parts = s.ToLowerInvariant().Replace("dpi", "").Split('x');
        int x = int.Parse(parts[0], CultureInfo.InvariantCulture);
        int y = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : x;
        if (!Iw2Pitches.TryFromDpi(x, out _)) throw new ArgumentException($"horizontal dpi {x} is not an ImageWriter density (72 80 96 107 120 136 144 160)");
        if (!Iw2Pitches.IsSupportedVerticalDpi(y)) throw new ArgumentException($"vertical dpi {y} must be 72 or 144");
        return new IppResolution(x, y);
    }

    private static (double w, double h) ParsePage(string? s)
    {
        if (s is null) return (8.5, 11);
        var parts = s.ToLowerInvariant().Replace("in", "").Split('x');
        return (double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static void SendPages(Args a, IEnumerable<RasterPage> pages, bool color)
    {
        using var port = OpenOutput(a);
        using var stream = new PrinterPortStream(port);
        var enc = new Iw2JobEncoder(stream, EncoderOptions(a, color));
        enc.Progress = (done, total) => { if (done % 10 == 0 || done == total) Console.Error.Write($"\r  band {done}/{total}   "); };
        enc.BeginJob();
        int n = 0;
        foreach (var page in pages)
        {
            n++;
            Console.Error.WriteLine($"page {n}: {page.Width}x{page.Height} @ {page.DpiX}x{page.DpiY} dpi{(page.IsColor ? " colour" : "")}");
            enc.EncodePage(page);
            Console.Error.WriteLine();
        }
        enc.EndJob();
        stream.Flush();
        port.Flush();
        Console.Error.WriteLine($"done: {stream.TotalBytes:N0} bytes -> {port.Description}");
    }

    // ------------------------------------------------------------------ commands

    private static int PortStatus(Args a)
    {
        var settings = new SerialPortSettings { PortName = a.Get("port") ?? "COM1", BaudRate = int.Parse(a.Get("baud") ?? "9600", CultureInfo.InvariantCulture), Handshake = Iw2Handshake.Auto };
        using var port = new SerialPrinterPort(settings);
        port.Open();
        Thread.Sleep(200);
        Console.WriteLine($"{settings.PortName}: {port.LineStatus}");
        Console.WriteLine($"ready line: {port.ReadyLine}   ready: {(port.IsReady ? "yes" : "NO")}");
        Console.WriteLine(port.Recommendation());
        return port.IsReady ? 0 : 2;
    }

    private static int Identify(Args a)
    {
        using var port = OpenOutput(a);
        var id = Iw2Identification.Query(port, TimeSpan.FromSeconds(double.Parse(a.Get("timeout") ?? "3", CultureInfo.InvariantCulture)));
        if (id is null)
        {
            Console.WriteLine("no answer (printer off, busy, deselected, or the cable has no printer->PC data line)");
            return 2;
        }
        Console.WriteLine(id);
        return 0;
    }

    private static int Text(Args a)
    {
        if (a.Positional.Count < 2) throw new ArgumentException("text: file name required");
        var data = File.ReadAllBytes(a.Positional[1]);
        var o = new TextJobOptions
        {
            Font = a.Has("nlq") ? Iw2Font.NearLetterQuality : a.Has("draft") ? Iw2Font.Draft : Iw2Font.Correspondence,
            Pitch = a.Has("elite") ? Iw2Pitch.Elite : a.Has("condensed") ? Iw2Pitch.Condensed : a.Has("ultra") ? Iw2Pitch.Ultracondensed : Iw2Pitch.Pica,
            LinesPerInch = a.Has("8lpi") ? 8 : 6,
            Raw = a.Has("raw"),
            FormFeedAtEnd = !a.Has("no-ff")
        };
        var buf = new ArrayBufferWriter<byte>();
        TextJobEncoder.Encode(data, buf, o);
        using var port = OpenOutput(a);
        port.Write(buf.WrittenSpan);
        port.Flush();
        Console.Error.WriteLine($"sent {buf.WrittenCount:N0} bytes");
        return 0;
    }

    private static int Raw(Args a)
    {
        if (a.Positional.Count < 2) throw new ArgumentException("raw: file name required");
        var data = File.ReadAllBytes(a.Positional[1]);
        using var port = OpenOutput(a);
        port.Write(data);
        port.Flush();
        Console.Error.WriteLine($"sent {data.Length:N0} bytes");
        return 0;
    }

    private static int Image(Args a)
    {
        if (a.Positional.Count < 2) throw new ArgumentException("image: file name required");
        var res = ParseRes(a.Get("dpi"));
        bool color = a.Has("color");
        var (pw, ph) = ParsePage(a.Get("page"));
        double left = double.Parse(a.Get("left") ?? "0.25", CultureInfo.InvariantCulture);
        double margin = double.Parse(a.Get("margin") ?? "0.5", CultureInfo.InvariantCulture);
        double? width = a.Get("width") is { } w ? double.Parse(w, CultureInfo.InvariantCulture) : null;

        using var src = new Bitmap(a.Positional[1]);
        using var page = RenderImagePage(src, pw, ph, res, left, margin, width);
        var raster = BitmapToPage(page, res, color, HalftoneOptions(a), pw, ph);
        SendPages(a, [raster], color);
        return 0;
    }

    private static Bitmap RenderImagePage(Bitmap src, double pw, double ph, IppResolution res, double left, double margin, double? widthInches)
    {
        int W = (int)Math.Round(pw * res.X), H = (int)Math.Round(ph * res.Y);
        var page = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(page);
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        double printableLeft = Math.Max(left, margin), printableTop = margin;
        double printableW = Math.Min(pw - printableLeft - margin, Iw2.PrintWidthInches - (printableLeft - left));
        double printableH = ph - 2 * margin;
        double srcAspect = src.Width * (src.VerticalResolution > 0 ? 1.0 : 1.0) / (double)src.Height;
        double wIn = widthInches ?? printableW;
        double hIn = wIn / srcAspect;
        if (hIn > printableH) { hIn = printableH; wIn = hIn * srcAspect; }
        double x = printableLeft + (printableW - wIn) / 2, y = printableTop;
        g.DrawImage(src, (float)(x * res.X), (float)(y * res.Y), (float)(wIn * res.X), (float)(hIn * res.Y));
        return page;
    }

    private static RasterPage BitmapToPage(Bitmap bmp, IppResolution res, bool color, HalftoneOptions halftone, double pw, double ph)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var rgb = new byte[w * 3 * h];
            for (int y = 0; y < h; y++)
            {
                var row = new byte[w * 3];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w * 3);
                for (int x = 0; x < w; x++)
                {
                    // GDI+ stores BGR
                    rgb[(y * w + x) * 3] = row[x * 3 + 2];
                    rgb[(y * w + x) * 3 + 1] = row[x * 3 + 1];
                    rgb[(y * w + x) * 3 + 2] = row[x * 3];
                }
            }
            var header = PwgRasterWriter.RgbHeader(w, h, res.X, res.Y, (int)Math.Round(pw * 72), (int)Math.Round(ph * 72));
            return RasterPageBuilder.Build(header, rgb, color, halftone);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static int TestPage(Args a)
    {
        var res = ParseRes(a.Get("dpi"));
        bool color = a.Has("color");
        var (pw, ph) = ParsePage(a.Get("page"));
        using var bmp = TestPageRenderer.Render(pw, ph, res, color, a.Get("text") ?? "");
        if (a.Get("png") is { } png) { bmp.Save(png, ImageFormat.Png); Console.Error.WriteLine($"wrote {png}"); }
        var halftone = HalftoneOptions(a);
        halftone.Mode = a.Get("dither") is null ? DitherMode.FloydSteinberg : halftone.Mode;
        var raster = BitmapToPage(bmp, res, color, halftone, pw, ph);
        SendPages(a, [raster], color);
        return 0;
    }

    private static int Pwg(Args a)
    {
        if (a.Positional.Count < 2) throw new ArgumentException("pwg: file name required");
        bool color = a.Has("color");
        var halftone = HalftoneOptions(a);
        using var file = File.OpenRead(a.Positional[1]);
        var reader = new RasterStreamReader(file);
        IEnumerable<RasterPage> Pages()
        {
            while (reader.ReadPageHeader() is { } h)
            {
                Console.Error.WriteLine($"  {h}");
                var pixels = new byte[(long)h.BytesPerLine * h.Height];
                reader.ReadPagePixels(h, pixels);
                yield return RasterPageBuilder.Build(h, pixels, color, halftone);
            }
        }
        SendPages(a, Pages(), color);
        return 0;
    }

    private static int MakePwg(Args a)
    {
        if (a.Positional.Count < 3) throw new ArgumentException("make-pwg: <image> <out.pwg> required");
        var res = ParseRes(a.Get("dpi"));
        string mediaName = a.Get("media") ?? "na_letter_8.5x11in";
        var media = ImageWriterII.Ipp.Server.PwgMedia.Parse(mediaName);
        double pw = media.WidthInches, ph = media.HeightInches;
        using var src = new Bitmap(a.Positional[1]);
        using var page = RenderImagePage(src, pw, ph, res, 0.25, double.Parse(a.Get("margin") ?? "0.5", CultureInfo.InvariantCulture), a.Get("width") is { } w ? double.Parse(w, CultureInfo.InvariantCulture) : null);
        int W = page.Width, H = page.Height;
        var data = page.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var rgb = new byte[W * 3 * H];
        try
        {
            for (int y = 0; y < H; y++)
            {
                var row = new byte[W * 3];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, W * 3);
                for (int x = 0; x < W; x++) { rgb[(y * W + x) * 3] = row[x * 3 + 2]; rgb[(y * W + x) * 3 + 1] = row[x * 3 + 1]; rgb[(y * W + x) * 3 + 2] = row[x * 3]; }
            }
        }
        finally { page.UnlockBits(data); }

        using var outFile = File.Create(a.Positional[2]);
        using var writer = new PwgRasterWriter(outFile);
        int copies = int.Parse(a.Get("copies") ?? "1", CultureInfo.InvariantCulture);
        if (a.Has("bilevel"))
        {
            var plane = Halftone.DitherRgbToGray(rgb, W, H, W * 3, HalftoneOptions(a));
            var hdr = PwgRasterWriter.BilevelHeader(W, H, res.X, res.Y, media.WidthPoints, media.HeightPoints, mediaName);
            hdr = new RasterPageHeader { Width = W, Height = H, BitsPerColor = 1, BitsPerPixel = 1, BytesPerLine = plane.Stride, ColorSpace = RasterColorSpace.K, NumColors = 1, HwResolutionX = res.X, HwResolutionY = res.Y, PageWidthPoints = media.WidthPoints, PageHeightPoints = media.HeightPoints, NumCopies = copies, TotalPageCount = 1, MediaSizeName = mediaName };
            writer.WritePage(hdr, plane.Data);
        }
        else if (a.Has("rgb"))
        {
            var hdr = PwgRasterWriter.RgbHeader(W, H, res.X, res.Y, media.WidthPoints, media.HeightPoints, mediaName);
            writer.WritePage(hdr, rgb);
        }
        else
        {
            var gray = new byte[W * H];
            for (int i = 0; i < W * H; i++) gray[i] = (byte)((rgb[i * 3] * 299 + rgb[i * 3 + 1] * 587 + rgb[i * 3 + 2] * 114 + 500) / 1000);
            var hdr = PwgRasterWriter.GrayHeader(W, H, res.X, res.Y, media.WidthPoints, media.HeightPoints, mediaName);
            writer.WritePage(hdr, gray);
        }
        Console.Error.WriteLine($"wrote {a.Positional[2]} ({outFile.Length:N0} bytes)");
        return 0;
    }

    private static int Render(Args a)
    {
        if (a.Positional.Count < 3) throw new ArgumentException("render: <file.iw> <out.png> required");
        byte[] data;
        using (var fs = new FileStream(a.Positional[1], FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { using var ms = new MemoryStream(); fs.CopyTo(ms); data = ms.ToArray(); }
        int dpi = int.Parse(a.Get("dpi") ?? "144", CultureInfo.InvariantCulture);
        var (pw, ph) = ParsePage(a.Get("page"));
        var sim = new Iw2Simulator(new SimulatorOptions
        {
            DpiX = dpi,
            DpiY = dpi,
            PageWidthInches = pw,
            PageHeightInches = ph,
            LeftEdgeOffsetInches = double.Parse(a.Get("left") ?? "0.25", CultureInfo.InvariantCulture),
            DotDiameterInches = double.Parse(a.Get("dot") ?? (dpi > 200 ? "0.0125" : "0"), CultureInfo.InvariantCulture)
        });
        var pages = sim.Run(data);
        foreach (var w in sim.Log.Distinct().Take(20)) Console.Error.WriteLine("  sim: " + w);
        if (pages.Count == 0) { Console.Error.WriteLine("nothing printed"); return 2; }
        string outPath = a.Positional[2];
        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            var rgb = p.ToRgb();
            using var bmp = new Bitmap(p.Width, p.Height, PixelFormat.Format24bppRgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, p.Width, p.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[p.Width * 3];
                for (int y = 0; y < p.Height; y++)
                {
                    for (int x = 0; x < p.Width; x++) { row[x * 3] = rgb[(y * p.Width + x) * 3 + 2]; row[x * 3 + 1] = rgb[(y * p.Width + x) * 3 + 1]; row[x * 3 + 2] = rgb[(y * p.Width + x) * 3]; }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, row.Length);
                }
            }
            finally { bmp.UnlockBits(bd); }
            bmp.SetResolution(dpi, dpi);
            string path = pages.Count == 1 ? outPath : Path.Combine(Path.GetDirectoryName(outPath) ?? "", $"{Path.GetFileNameWithoutExtension(outPath)}-{i + 1}{Path.GetExtension(outPath)}");
            bmp.Save(path, ImageFormat.Png);
            Console.Error.WriteLine($"page {i + 1}: {p.Dots:N0} dots -> {path}{(p.Warnings.Count > 0 ? $" ({p.Warnings.Count} warnings)" : "")}");
        }
        return 0;
    }

    // ------------------------------------------------------------------ IPP client

    private static int Ipp(Args a)
    {
        if (a.Positional.Count < 3) throw new ArgumentException("ipp: <attrs|jobs|print> <url> required");
        string sub = a.Positional[1].ToLowerInvariant();
        string url = a.Positional[2];
        if (url.StartsWith("ipp://", StringComparison.OrdinalIgnoreCase)) url = "http://" + url[6..];
        string printerUri = "ipp://" + url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        IppMessage Send(IppMessage req, Stream? doc)
        {
            var body = new MemoryStream();
            IppCodec.Write(req, body);
            doc?.CopyTo(body);
            body.Position = 0;
            var content = new StreamContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");
            var resp = http.PostAsync(url, content).GetAwaiter().GetResult();
            Console.Error.WriteLine($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return IppCodec.Read(new MemoryStream(bytes));
        }

        IppMessage NewRequest(IppOperation op)
        {
            var m = new IppMessage { VersionMajor = 2, VersionMinor = 0, OperationOrStatus = (ushort)op, RequestId = Random.Shared.Next(1, 100000) };
            var g = m.AddGroup(IppTag.OperationAttributes);
            g.Add("attributes-charset", IppValue.Charset("utf-8"));
            g.Add("attributes-natural-language", IppValue.Language("en"));
            g.Add("printer-uri", IppValue.Uri(printerUri));
            g.Add("requesting-user-name", IppValue.Name(Environment.UserName));
            return m;
        }

        switch (sub)
        {
            case "attrs":
                {
                    var req = NewRequest(IppOperation.GetPrinterAttributes);
                    if (a.Get("requested") is { } r) req.Group(IppTag.OperationAttributes)!.Add("requested-attributes", r.Split(',').Select(IppValue.Keyword).ToArray());
                    var resp = Send(req, null);
                    Console.WriteLine(resp);
                    return resp.Status == IppStatus.SuccessfulOk ? 0 : 2;
                }
            case "jobs":
                {
                    var req = NewRequest(IppOperation.GetJobs);
                    req.Group(IppTag.OperationAttributes)!.Add("which-jobs", IppValue.Keyword(a.Get("which") ?? "all"));
                    req.Group(IppTag.OperationAttributes)!.Add("requested-attributes", IppValue.Keyword("all"));
                    var resp = Send(req, null);
                    Console.WriteLine(resp);
                    return 0;
                }
            case "print":
                {
                    if (a.Positional.Count < 4) throw new ArgumentException("ipp print: <url> <file> required");
                    string file = a.Positional[3];
                    string format = a.Get("format") ?? (file.EndsWith(".pwg", StringComparison.OrdinalIgnoreCase) ? "image/pwg-raster" : file.EndsWith(".urf", StringComparison.OrdinalIgnoreCase) ? "image/urf" : file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "text/plain" : "application/octet-stream");
                    var req = NewRequest(IppOperation.PrintJob);
                    var og = req.Group(IppTag.OperationAttributes)!;
                    og.Add("job-name", IppValue.Name(a.Get("name") ?? Path.GetFileName(file)));
                    og.Add("document-format", IppValue.MimeType(format));
                    var jg = req.AddGroup(IppTag.JobAttributes);
                    if (a.Get("copies") is { } c) jg.Add("copies", IppValue.Integer(int.Parse(c, CultureInfo.InvariantCulture)));
                    if (a.Get("res") is { } rs) { var r = ParseRes(rs); jg.Add("printer-resolution", IppValue.Resolution(r.X, r.Y)); }
                    jg.Add("print-color-mode", IppValue.Keyword(a.Has("color") ? "color" : "monochrome"));
                    if (a.Get("media") is { } md) jg.Add("media", IppValue.Keyword(md));
                    using var doc = File.OpenRead(file);
                    var resp = Send(req, doc);
                    Console.WriteLine(resp);
                    return resp.Status is IppStatus.SuccessfulOk or IppStatus.SuccessfulOkIgnoredOrSubstitutedAttributes ? 0 : 2;
                }
            default:
                throw new ArgumentException($"ipp: unknown subcommand '{sub}'");
        }
    }
}

/// <summary>Minimal argument parser: positionals plus --name value / --flag.</summary>
internal sealed class Args
{
    private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positional { get; } = [];

    public Args(string[] argv)
    {
        for (int i = 0; i < argv.Length; i++)
        {
            string s = argv[i];
            if (s.StartsWith("--", StringComparison.Ordinal) || (s.StartsWith('-') && s.Length > 1 && !char.IsDigit(s[1])))
            {
                string key = s.TrimStart('-');
                int eq = key.IndexOf('=');
                if (eq >= 0) { _opts[key[..eq]] = key[(eq + 1)..]; continue; }
                if (i + 1 < argv.Length && !argv[i + 1].StartsWith('-')) { _opts[key] = argv[++i]; }
                else _opts[key] = null;
            }
            else Positional.Add(s);
        }
    }

    public bool Has(string name) => _opts.ContainsKey(name);
    public string? Get(string name) => _opts.TryGetValue(name, out var v) ? v : null;
}
