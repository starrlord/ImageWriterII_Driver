# ImageWriter II driverless printing for Windows 11

A small Windows service that turns an Apple ImageWriter II on a serial port into an
**IPP Everywhere / AirPrint printer**. Windows 11 talks to it with the built-in
*Microsoft IPP Class Driver*, so there is no vendor driver, no INF and nothing to sign.
macOS, iOS, Linux (CUPS) and Android see it as a Bonjour printer too.

The service rasterises the pages it receives (PWG raster from Windows, Apple raster from macOS/iOS)
into the printer's native bit-image commands and uses **every graphics capability the ImageWriter II has**:

| Capability | How it is used |
|---|---|
| All eight horizontal densities: 72, 80, 96, 107, 120, 136, 144, 160 dpi | pitch commands ESC n / N / E / e / q / Q / p / P; any of them can be offered to clients (`Resolutions`) |
| 144 dpi vertical | two interleaved passes 1/144" apart (ESC T 01), 16-row bands |
| 160 x 144 dpi (the Mac "Best" quality) | offered as a resolution, exact non-square rendering by the client |
| Dot-column repeat (ESC V) and head positioning (ESC F) | run-length compression and blank skipping: a text page is ~50 KB instead of 230 KB |
| Grouped graphics (ESC g) | faster printer parsing when literal runs are multiples of eight |
| Four-colour ribbon (ESC K) | Y, M, C, K separation with grey-component replacement, yellow first as the manual says; ribbon auto-detected with ESC ? |
| Uni/bidirectional head motion (ESC > / <) | unidirectional by default for perfect interleave registration; draft quality switches to bidirectional |
| Page length (ESC H), top of form (ESC v), reverse feed (ESC r) | any media from 2x2" to 8.5x17", per-page colour strategy for tractor paper |
| Software switches (ESC Z / ESC D) | forces no auto-LF, no perforation skip, 8-bit data regardless of the DIP switches |
| Built-in fonts (ESC a, pitches, 6/8 lpi) | plain-text jobs print with the printer's own Draft / Correspondence / NLQ fonts |

Everything is derived from the *Apple ImageWriter II Technical Reference Manual* (see
`docs/printer-protocol.md`), and every escape stream the encoder produces is checked dot-for-dot
by a built-in printer simulator in the unit tests.

## Contents

```
src/ImageWriterII.Core     printer command set, PWG/URF raster decoding, halftoning, encoder, simulator, serial port
src/ImageWriterII.Ipp      IPP protocol, printer object, job spooler, DNS-SD (mDNS) responder, raw TCP 9100 port
src/ImageWriterII.Service  the Windows service / console host (Kestrel on port 631, status page)
src/iwprint                command-line tool: test pages, images, text, identity query, render, IPP client, mDNS browse
tests/                     xunit tests (encoder round trips through the simulator, codecs, IPP, DNS)
scripts/                   install-service.ps1, uninstall-service.ps1, add-printer.ps1, remove-printer.ps1
docs/                      hardware-setup.md (cable, DIP switches), printer-protocol.md (what is sent and why)
```

Requires the .NET 10 SDK to build (`dotnet build ImageWriterII.slnx`), .NET 10 runtime to run.

## Quick start

1. **Hardware.** Printer DIP switches at factory settings: 9600 baud (SW2-1 and SW2-2 closed),
   hardware handshake (SW2-3 open). Cable: Mini-DIN-8 to RS-232 with the printer's **DTR (pin 2)
   reaching the PC on CTS, DSR or DCD** (the service detects which). Details and pinout in
   `docs/hardware-setup.md`. Check the link:

   ```
   iwprint status --port COM1      # shows CTS/DSR/DCD; one must be high when the printer is on and selected
   iwprint identify --port COM1    # prints e.g. "IW10C: ImageWriter, 10-inch carriage, colour ribbon" (needs the printer->PC data line)
   ```

2. **Install the service** (elevated PowerShell, from the repository root):

   ```powershell
   .\scripts\install-service.ps1 -Port COM1            # add -Handshake DataSetReady etc. to override auto-detection
   ```

   This publishes to `C:\Program Files\ImageWriterII`, registers the `ImageWriterII` service
   (automatic start, restart on failure), opens the firewall for TCP 631 / UDP 5353 / TCP 9100 and starts it.
   The status page is at <http://localhost:631/>.

3. **Add the printer to Windows** (elevated PowerShell):

   ```powershell
   .\scripts\add-printer.ps1
   ```

   which runs `Add-Printer -Name "ImageWriter II" -IppURL http://localhost:631/ipp/print`.
   Or use Settings > Bluetooth & devices > Printers & scanners > *Add device*: the printer is announced
   with DNS-SD, so it should be listed by name; if not, *Add manually* > *Select a shared printer by name*
   > `http://localhost:631/ipp/print`. Windows installs it with the Microsoft IPP Class Driver.

4. **Print.** Print quality in the Windows dialog maps to the resolutions the service offers
   (72x72 draft, 144x144 default, 160x144 best). Paper sizes come from `MediaSupported`.

### Console mode and testing without paper

Run `ImageWriterII.Service.exe` from a prompt to see the log live. Any setting can be overridden on the
command line, and a file can stand in for the printer:

```
ImageWriterII.Service.exe --ImageWriter:HttpPort=8631 "--ImageWriter:Serial:PortName=file:D:\out.iw"
iwprint ipp attrs http://localhost:8631/ipp/print
iwprint make-pwg photo.jpg photo.pwg --dpi 144x144 && iwprint ipp print http://localhost:8631/ipp/print photo.pwg
iwprint render D:\out.iw out.png            # what the printer would have printed
```

`iwprint testpage --out page.iw --png page.png` followed by `iwprint render page.iw page-render.png`
shows the alignment test page exactly as the printer will produce it.

## Configuration

`appsettings.json` next to the service executable (comments allowed). The important knobs:

| Setting | Meaning |
|---|---|
| `Serial.PortName`, `BaudRate`, `Handshake` | COM port, 9600, `Auto` (CTS/DSR/DCD detected), `RequestToSend`, `DataSetReady`, `DataCarrierDetect`, `XOnXOff`, `None` |
| `Ribbon` | `Auto` (ESC ? at start), `Black`, `Color` |
| `Resolutions`, `DefaultResolution` | what clients may choose; any ImageWriter density x 72 or 144 |
| `Encoder.LeftEdgeOffsetInches` | where dot column 0 lands on the sheet (0.25" for letter against the guide) |
| `Encoder.Bidirectional` | faster, unidirectional keeps the two 144-dpi passes perfectly registered |
| `Encoder.ColorStrategy` | `PerBand` (default) or `PerPage` (tractor paper only) |
| `Halftone.Gamma`, `Mode` | dot-gain compensation (1.8) and Floyd-Steinberg / ordered / threshold |
| `Text.Font`, `Pitch`, `LinesPerInch` | printer fonts for plain-text jobs (raw port, `text/plain` IPP jobs) |
| `MediaSupported`, margins | PWG media names offered; 1/4" unprintable margins reported |
| `HttpPort`, `RawPort`, `MdnsEnabled`, `HostName` | 631, 9100, announce as `<HostName>.local` |

## How it works

```
Windows (IPP Class Driver) --PWG raster over IPP/HTTP--> Kestrel --> spool file --> job queue
                                                                        |
                              PWG/URF decode -> dither (or 1-bit copy) -> Iw2JobEncoder -> COM1
```

* `Iw2JobEncoder` walks the page in 1/9" bands (8 pins x 1/72"), builds one byte per column with bit 0 as the
  top pin, and emits ESC g/G literals, ESC V repeats and ESC F skips, then the 1/144" and 15/144" feeds.
  Blank bands become accumulated line feeds; the page ends with FF against the ESC H page length.
* Windows renders `sgray_8` at the requested resolution; the service dithers with Floyd-Steinberg after a
  dot-gain curve because the printer's dots overlap heavily on the 144 dpi grid. If a client sends `black_1`
  the bits go straight to the printer.
* Flow control is the printer's DTR line: writes simply block while it is busy or out of paper, and resume
  when it is ready, so a job never loses bytes.
* Discovery: a self-contained mDNS/DNS-SD responder answers `_ipp._tcp`, `_universal._sub._ipp._tcp`,
  `_print._sub._ipp._tcp`, `_pdl-datastream._tcp` and `_http._tcp` with the address of the interface the query
  came in on. `iwprint mdns` browses like `dns-sd -B`.

## Troubleshooting

* **Nothing prints, job stays "Printing", no TX light on the USB adapter.** Flow control is waiting on a
  line the printer's ready signal never reaches. `iwprint status` shows CTS/DSR/DCD and the setting to use;
  `Handshake: Auto` picks it automatically. If no line is high the printer is off/deselected or the cable
  carries no DTR at all: use XON/XOFF (SW2-3 closed) or `None` with `MaxBytesPerSecond` 600.
* **Status page says "Printer not ready" or "stopped accepting data".** Same causes; the job resumes by
  itself once the printer comes on-line.
* **Ready line is high but still nothing prints.** The cable is a crossed "ImageWriter I" type; add a
  DB-25 null-modem adapter (see `docs/hardware-setup.md`).
* **Garbage or missing chunks.** Flow control is not working (see above). As a stop-gap set
  `Serial.MaxBytesPerSecond` to 600; the printer's 2K buffer then never overruns at print speed.
* **Output shifted left/right.** Adjust `Encoder.LeftEdgeOffsetInches` using the test page border.
* **Windows does not list the printer.** Add it by URL (step 3). Discovery on the same PC depends on
  Windows's own mDNS resolver; other machines on the LAN discover it normally.
* **Ribbon shows as black although a colour ribbon is installed.** The cable has no printer-to-PC data
  line; set `Ribbon` to `Color`.
* Logs: `logs\imagewriter-YYYYMMDD.log` next to the executable; the status page lists jobs and lets you cancel them.

## Known limitations

* Only raster input: PWG raster and Apple raster (plus plain text and native ImageWriter data). No PDF or PCLm,
  so clients that can only send PDF (some Android apps) will not work; Windows, macOS, iOS and CUPS all send raster.
* IPP over plain HTTP only (`_ipp._tcp`); no TLS / `_ipps._tcp`. Fine on a home LAN, not for the open internet.
* The DNS-SD responder is IPv4 only, and discovery *from the same PC* depends on Windows's own mDNS resolver;
  adding the queue by URL always works.
* One document per job; copies are handled by re-sending pages.
* DSR-based hardware handshake uses the Windows serial API; on other platforms `Auto` falls back to software gating.
* Tested on one ImageWriter II with an FTDI USB adapter. Reports from other cables and adapters are welcome.

## License

This project is licensed under the [MIT License](LICENSE).

## Credits

* Apple, *ImageWriter II Technical Reference Manual*, 1987 (command set, timing, interface).
* Daniele Cattaneo's macOS ImageWriter II CUPS driver and farlepet's `iwiitool` for confirming the
  144-dpi interleave, repeat commands and colour sequencing on real hardware.
* CUPS `ippeveprinter` and PAPPL for the IPP Everywhere attribute set that Windows and Apple clients accept.
