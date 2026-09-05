# TODO

Status (2026-09-04): the service is installed, the Windows queue uses the Microsoft IPP Class Driver, and a
Windows test page printed on the real ImageWriter II over COM1 (DSR hardware handshake, Handshake = Auto).

Three things worth doing next, in order of payoff:

1. **Alignment and tone check.** Print the built-in test page from the repo root; it shows the printable-area
   border, inch ticks, circles, single-dot line pairs and a gray ramp:

   ```
   .\publish\iwprint.exe testpage --port COM1 --dpi 144x144
   ```

   If the border sits off-centre, change `Encoder.LeftEdgeOffsetInches` in
   `C:\Program Files\ImageWriterII\appsettings.json` (larger moves the image left). If mid-grays print too
   dark or too light, raise or lower `Halftone.Gamma` (1.8 now). Restart the service after editing:
   `Restart-Service ImageWriterII` (elevated).

2. **Best quality.** In Printing preferences pick the 160x144 quality; that is the resolution the original
   Mac driver used for "Best". Draft (72x72) prints about four times faster and switches to bidirectional.

3. **Colour.** Set `"Ribbon": "Color"` in the config, restart the service, then re-add the Windows queue with
   `.\scripts\add-printer.ps1` so Windows re-reads the printer's capabilities (elevated PowerShell):

   ```powershell
   $p = "C:\Program Files\ImageWriterII\appsettings.json"
   (Get-Content $p -Raw) -replace '"Ribbon":\s*"[^"]*"', '"Ribbon": "Color"' | Set-Content $p -Encoding UTF8
   Restart-Service ImageWriterII
   .\scripts\add-printer.ps1
   ```

If the fine line pairs on the test page look doubled or smeared, that points at bidirectional registration or a
paper-feed issue; adjust `Encoder.Bidirectional` (keep it false for quality) or check the paper path.

Still unverified: same-PC mDNS discovery (the queue was added by URL), colour printing, and the two tuning
values above.
