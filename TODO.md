# TODO

Status (2026-09-05): the service is installed and a Windows test page prints in **colour** on the real
ImageWriter II over COM1 (DSR hardware handshake, `Handshake: Auto`). Colour is now the shipped default:
`install-service.ps1` writes `"Ribbon": "Color"` and `add-printer.ps1` pins the Windows queue to colour.

## Tuning worth doing on real paper

1. **Alignment and tone.** Print the built-in test page; it shows the printable-area border, inch ticks,
   circles, single-dot line pairs and a gray ramp:

   ```
   .\publish\iwprint.exe testpage --port COM1 --dpi 144x144 --color
   ```

   If the border sits off-centre, change `Encoder.LeftEdgeOffsetInches` in
   `C:\Program Files\ImageWriterII\appsettings.json` (larger moves the image left). If mid-grays print too
   dark or too light, raise or lower `Halftone.Gamma` (1.8 now). Restart the service after editing:
   `Restart-Service ImageWriterII` (elevated).

2. **Colour tone.** The separation uses full grey-component replacement, so neutrals print with the black
   band only. If saturated areas look muddy, `Halftone.Gamma` affects all four planes together.

3. **Best quality.** In Printing preferences pick 160x144; that is the resolution the original Mac driver
   used for "Best". Draft (72x72) prints about four times faster and switches to bidirectional.

If the fine line pairs on the test page look doubled or smeared, that points at bidirectional registration or a
paper-feed issue; adjust `Encoder.Bidirectional` (keep it false for quality) or check the paper path.

## Known gaps, in rough order of value

These came out of the 2026-09-04 audit and are deliberately not fixed yet.

* **mDNS name compression.** `DnsWire` writes every name in full, so the unsolicited announcement exceeds
  the 1500-byte MTU and is IP-fragmented. It works (fragments reassemble) but RFC 6762 wants it to fit, and
  some minimal responders drop fragmented mDNS. Fix: back-reference pointers in `BuildResponse`.
* **No probing / name-conflict handling.** `MdnsResponder` claims its unique records without the RFC 6762 8.1
  probe sequence. Two ImageWriter II services on one LAN would both answer for `imagewriter-ii.local`;
  today the workaround is to give one of them a different `HostName`.
* **No IPv6.** The responder answers with A records only, so an IPv6-only network cannot find the printer.
* **No TLS / `_ipps._tcp`.** Fine on a home LAN, and current iOS and macOS still print to plain-IPP printers.
* **Web UI has no authentication or CSRF token.** Anyone who can reach port 631 can cancel a job or print.
  That matches an ordinary network printer and is a deliberate trade-off for a LAN device, but it is the
  reason not to port-forward this service.
* **`install-service.ps1` hardcodes 631 / 5353 / 9100** in its firewall rules rather than reading them from
  `appsettings.json`, so changing `HttpPort` or `RawPort` means fixing the rules by hand.
* **Untested paths.** There are no tests for the IPP operation handlers, the mDNS packet builder, or the
  serial flow-control state machine; the encoder, raster codecs and colour separation are covered.

Still unverified on hardware: same-PC mDNS discovery (the queue was added by URL), AirPrint from an actual
iPhone or iPad, and the `PerPage` colour strategy on tractor paper.
