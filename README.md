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
| Four-colour ribbon (ESC K) | Y, M, C, K separation with grey-component replacement, yellow first as the manual says; **colour is the default** (`Ribbon`) |
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
   .\scripts\install-service.ps1 -Port COM1 -Ribbon Black    # if a black ribbon is fitted
   ```

   This publishes to `C:\Program Files\ImageWriterII`, registers the `ImageWriterII` service
   (automatic start, restart on failure), opens the firewall for TCP 631 / UDP 5353 / TCP 9100 and starts it.
   The status page is at <http://localhost:631/>.

   The install defaults to the **four-colour ribbon** (`-Ribbon Color`), so colour printing works out of the
   box. See [Colour printing](#colour-printing) for why that is not auto-detected.

3. **Add the printer to Windows** (elevated PowerShell):

   ```powershell
   .\scripts\add-printer.ps1
   ```

   which runs `Add-Printer -Name "ImageWriter II" -IppURL http://localhost:631/ipp/print` and then pins the
   queue's default colour mode to whatever the service reports (`Set-PrintConfiguration -Color`).
   Or use Settings > Bluetooth & devices > Printers & scanners > *Add device*: the printer is announced
   with DNS-SD, so it should be listed by name; if not, *Add manually* > *Select a shared printer by name*
   > `http://localhost:631/ipp/print`. Windows installs it with the Microsoft IPP Class Driver.

   **Windows caches the printer's capabilities when the queue is created.** After changing `Ribbon`,
   `Resolutions` or `MediaSupported`, restart the service *and* re-run `add-printer.ps1`, or Windows keeps
   offering the old capabilities.

4. **Print.** Print quality in the Windows dialog maps to the resolutions the service offers
   (72x72 draft, 144x144 default, 160x144 best). Paper sizes come from `MediaSupported`.
   iPhone, iPad and Mac need no setup at all — see [AirPrint](#airprint-from-iphone-ipad-and-mac).

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

## AirPrint from iPhone, iPad and Mac

There is nothing to install on the Apple device. The service *is* the AirPrint printer: it answers
Bonjour queries itself and speaks the IPP that iOS and macOS expect, so an ImageWriter II from 1985
shows up in the iOS share sheet next to any modern printer.

### Print

1. Put the iPhone, iPad or Mac **on the same network segment as the Windows PC** — the same Wi-Fi SSID or
   the same wired LAN behind one router. Bonjour is link-local multicast; it does not cross subnets, VLANs,
   guest-network isolation, or most VPNs.
2. Make sure the ImageWriter II is switched on, has paper and the **Select** light is on. A deselected
   printer holds the serial line busy and the job just sits in the queue.
3. **iOS / iPadOS:** open anything printable, tap the share button, tap *Print*, tap *Printer* and pick
   **ImageWriter II**. Set copies and paper size, then *Print*.
   **macOS:** File > Print. The printer appears under *Nearby Printers*; the first print sets it up
   automatically with AirPrint (no driver, no PPD).
4. Watch the job at <http://localhost:631/> on the PC — it shows the live band-by-band progress and lets you
   cancel. The printer is slow: 9600 baud is about 960 bytes/s, so a full-coverage 144 dpi letter page takes
   roughly four minutes, which is also about as fast as the mechanism goes.

Colour, paper size, and the draft/normal/best quality setting all come through from the Apple print dialog.
Apple devices send **Apple Raster (`image/urf`)**, which the service decodes natively — the same code path as
the PWG raster Windows sends.

### What the service advertises

Confirmed on the wire with `iwprint mdns` and a raw DNS-SD query:

| | |
|---|---|
| Service types | `_ipp._tcp` on TCP 631, with the `_universal` subtype AirPrint looks for and a `_print` subtype; plus `_pdl-datastream._tcp` (TCP 9100) and `_http._tcp` for the status page |
| Host record | `<HostName>.local` (default `imagewriter-ii.local`), answered **per interface** with the address of the interface the query arrived on, so a multi-homed PC hands each network the address that works on it |
| Key TXT records | `rp=ipp/print`, `ty=Apple ImageWriter II`, `adminurl`, `pdl=image/pwg-raster,image/urf,application/octet-stream,text/plain`, `URF=V1.4,W8,SRGB24,CP1,IS1,MT1-2,OB9,PQ3-4-5,RS72-144`, `Color=T` (with a colour ribbon), `Duplex=F`, `UUID`, `PaperMax=legal-A4` |
| TTLs | 4500 s for PTR, 120 s for SRV/TXT/A with the cache-flush bit, and the 10 s / no-cache-flush form RFC 6762 requires for legacy unicast queries |

`SRGB24` only appears in `URF` — and `Color=T` only becomes `T` — when the service believes a colour ribbon
is fitted. That is what tells iOS to send colour, so a black-ribbon configuration makes Apple devices send
grayscale, which is what you want.

### Firewall

The Apple device connects from the LAN, so Windows Firewall has to allow the traffic on **the profile your
network is classified as**. Home Ethernet and Wi-Fi are very often *Public* on Windows, not *Private*.
`install-service.ps1` therefore creates its rules for every profile by default, still scoped to this one
program and to these ports:

| Port | Protocol | Why |
|---|---|---|
| 5353 | UDP | Bonjour / mDNS discovery. Without it the printer never appears in the iOS printer list. |
| 631 | TCP | IPP: the actual print traffic. |
| 9100 | TCP | Raw "JetDirect" socket, only needed by old software. Drop the rule if you do not use it. |

Narrow it with `.\scripts\install-service.ps1 -FirewallProfile Private,Domain` if your LAN is classified
Private and you would rather it stayed that way.

### Troubleshooting AirPrint

* **The printer never appears in the iOS printer list.** Almost always mDNS not reaching the phone.
  Check in order: UDP 5353 allowed on the *current* network profile (`Get-NetConnectionProfile` shows the
  category; `Get-NetFirewallRule -DisplayName "ImageWriterII*"` shows what was opened); phone and PC on the
  same subnet; "client isolation" / "AP isolation" off on the router's guest or IoT network; and
  `MdnsEnabled: true` in the config. `iwprint mdns` on the PC should list the printer.
* **It appears, then fails with "Unable to communicate with printer".** Discovery worked but TCP 631 did not.
  That is the firewall rule for port 631, or the wrong address being advertised: if the PC has VirtualBox,
  Hyper-V, WSL or VPN adapters, restrict announcements to the real LAN adapter with
  `"MdnsInterfaces": [ "Ethernet" ]` (a substring of the adapter name or description).
* **The job appears in the iOS queue and never finishes.** The printer is offline, out of paper, or
  deselected — the service blocks on the serial flow-control line and resumes by itself. The status page
  says which.
* **It prints in black although a colour ribbon is fitted.** The service is configured for a black ribbon,
  so it advertised `Color=F` and iOS sent grayscale. See [Colour printing](#colour-printing).
* **Everything works from a Mac but not an iPhone (or vice versa).** Check that both are on the same SSID —
  many routers put 2.4 GHz and 5 GHz, or a guest network, on separate isolated segments.

### Limits of the AirPrint support

* **Plain IPP only.** The service offers `_ipp._tcp` over HTTP on port 631; it does not offer `_ipps._tcp`
  or TLS. Current iOS and macOS still print happily to plain-IPP printers, but the traffic is unencrypted
  and unauthenticated — fine on a home LAN, not something to expose to the internet.
* **IPv4 only.** The responder answers with A records; there are no AAAA records, so an IPv6-only network
  will not find it.
* **No PDF.** The service accepts PWG raster, Apple raster, plain text and native ImageWriter data.
  AirPrint from iOS and macOS always sends raster, so this is not a problem in practice — but a third-party
  app or an Android client that can only emit PDF will not work.
* **One document per job**, no duplex, no stapling — it is a 1985 dot-matrix printer.

## Colour printing

The four-colour ribbon is the default. `install-service.ps1` writes `"Ribbon": "Color"` into
`appsettings.json`, the service then advertises `color-supported`, `print-color-mode-supported =
auto,color,monochrome`, `srgb_8` PWG raster, `SRGB24` in `urf-supported` and `Color=T` over Bonjour, and
`add-printer.ps1` pins the Windows queue's default to colour with `Set-PrintConfiguration -Color $true`.
The result is that a normal Windows print sends `print-color-mode=auto` with `srgb_8` raster and comes out
in colour, and iOS does the same.

Pages are separated into yellow, magenta, cyan and black with full grey-component replacement, so neutral
tones use only the black band, and each 1/9" band is printed yellow first (the manual's advice, to keep the
yellow stripe of the ribbon from being contaminated). Ink planes that are empty for a band are skipped
entirely, so a page of black text costs exactly what it would in monochrome.

**Why colour is not auto-detected.** The printer will tell you what ribbon it has — `ESC ?` returns
`IW10C` for the colour ribbon — but the answer comes back on Mini-DIN-8 pin 5, and most Mac-to-PC cables
never wire it to the PC's RxD. `Ribbon: Auto` then gets no answer and falls back to black, silently turning
colour off. Defaulting to `Color` is the better failure mode: with a black ribbon fitted the separation
still prints correctly, just in black.

If you have a black ribbon, say so — it is faster and avoids ribbon wear:

```powershell
.\scripts\install-service.ps1 -Ribbon Black       # or edit "Ribbon" in appsettings.json and restart the service
.\scripts\add-printer.ps1                         # required: Windows caches the capabilities
```

`Ribbon: Auto` remains available and is the right choice if your cable does carry pin 5 —
`iwprint identify --port COM1` tells you whether it does.

## Configuration

`appsettings.json` next to the service executable (comments allowed). The important knobs:

| Setting | Meaning |
|---|---|
| `Serial.PortName`, `BaudRate`, `Handshake` | COM port, 9600, `Auto` (CTS/DSR/DCD detected), `RequestToSend`, `DataSetReady`, `DataCarrierDetect`, `XOnXOff`, `None` |
| `Ribbon` | `Color` (default), `Black`, or `Auto` (ask with ESC ?, falls back to `Black` if the cable has no return line) |
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
* Windows renders `srgb_8` when the queue is set to colour and `sgray_8` when it is not; the service dithers
  with Floyd-Steinberg after a dot-gain curve because the printer's dots overlap heavily on the 144 dpi grid.
  If a client sends `black_1` the bits go straight to the printer.
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
* **Prints in black although a colour ribbon is installed.** The service is configured for a black ribbon
  (the status page's *Ribbon* row and `/api/status` show which). Set `"Ribbon": "Color"`, restart the service,
  and re-run `add-printer.ps1` — Windows caches the capabilities when the queue is created, so without the
  re-add the queue stays monochrome. Also check *Printing preferences > Color* on the queue.
* **iPhone/iPad/Mac cannot see or reach the printer.** See [Troubleshooting AirPrint](#troubleshooting-airprint).
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

Add a `LICENSE` file before publishing (MIT or BSD-2 are the usual choices for this kind of project; the referenced
macOS driver and iwiitool are MIT and were used as documentation only, no code was copied).

## Credits

* Apple, *ImageWriter II Technical Reference Manual*, 1987 (command set, timing, interface).
* Daniele Cattaneo's macOS ImageWriter II CUPS driver and farlepet's `iwiitool` for confirming the
  144-dpi interleave, repeat commands and colour sequencing on real hardware.
* CUPS `ippeveprinter` and PAPPL for the IPP Everywhere attribute set that Windows and Apple clients accept.
