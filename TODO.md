# TODO

Status (2026-09-05): working end to end on real hardware. Colour printing is confirmed on paper from
**Windows** (Microsoft IPP Class Driver, `srgb_8` PWG raster) and from an **iPhone over AirPrint**
(Apple Raster). Colour is the shipped default: `install-service.ps1` writes `"Ribbon": "Color"` and
`add-printer.ps1` pins the Windows queue's colour mode.

## Tuning worth doing on real paper

Print the built-in test page — printable-area border, inch ticks, circles, single-dot line pairs and a gray
ramp — and adjust against it:

```
.\publish\iwprint.exe testpage --port COM1 --dpi 144x144 --color
```

* **Alignment.** If the border sits off-centre, change `Encoder.LeftEdgeOffsetInches` in
  `C:\Program Files\ImageWriterII\appsettings.json`; larger moves the image left.
* **Tone.** If mid-grays print too dark or too light, raise or lower `Halftone.Gamma` (1.8 now). It applies
  to all four ink planes together, so it is also the knob for muddy saturated colour.
* **Best quality.** In Printing preferences pick 160x144, the resolution the original Mac driver used for
  "Best". Draft (72x72) prints about four times faster and switches to bidirectional.

Restart the service after editing: `Restart-Service ImageWriterII` (elevated). If the fine line pairs look
doubled or smeared, that is bidirectional registration or paper feed — keep `Encoder.Bidirectional` false
and check the paper path.

## Known gaps

None outstanding. Everything the 2026-09-04 audit raised has been closed; see *Closed* below for what was
done and how each fix was verified.

## Deliberate limitations, not planned

* **No IPv6 — not planned.** *Discovery* is IPv4-only: one `AF_INET` socket on 224.0.0.251:5353, interfaces
  with no IPv4 address are skipped, only A records are emitted, and an AAAA query is answered with an NSEC
  saying A is all that exists. The **listeners are already dual-stack** (`ListenAnyIP` for HTTP/631, a
  dual-mode `TcpListener` for raw/9100), so IPP over IPv6 works if the client is handed the address — it
  simply cannot be found automatically. On an IPv6-only network the responder does not start at all.
* **No TLS / `_ipps._tcp`.** Only `_ipp._tcp` on plain HTTP, plus the plaintext raw port 9100 as
  `_pdl-datastream._tcp`. Current iOS and macOS still print happily to plain-IPP printers, and this is a LAN
  device.
* **No authentication or CSRF token.** Anyone who can reach port 631 can print, cancel any job
  (`POST /api/jobs/{id}/cancel` is a plain form post with no token) and read every retained job's document
  name and username. `uri-authentication-supported` is `none`; port 9100 is likewise open. Job and user names
  *are* HTML-escaped on the status page, so there is no XSS. This matches how an ordinary network printer
  behaves and is the reason not to port-forward the service.
* **No mDNS tie-breaking (RFC 6762 8.2).** Two responders probing for the same name at the same instant do
  not compare their proposed records to decide who wins; both simply see the other's announcement afterwards
  and one renames. Convergence is a second slower than the RFC's ideal, which does not matter for a printer.

## Still unverified on hardware

* **`PerPage` colour on tractor paper.** The rewind arithmetic is pinned by
  `ColorPerPageWithBlankLowerHalfKeepsInksRegistered` and `ColorPerPageWithAnEmptyInkPlaneDoesNotOverReverse`,
  but the simulator models paper feed as exact arithmetic with no backlash or slip, so nothing in the tests
  can show whether the mechanism really lands the paper where the reverse feeds ask. `PerBand` is the default
  and keeps registration exact, so this only affects someone who opts in.
* **Same-PC mDNS discovery.** Whether Windows's own resolver lists the queue on the machine running the
  service. A working iPhone print already proves the responder is discoverable by a real Bonjour client over
  the LAN; only the loopback case is untried. Adding the queue by URL always works, and the README documents
  that.

## Closed

* **mDNS name compression** — `DnsWire` now writes owner names, and the names inside PTR and SRV rdata, as
  RFC 1035 compression pointers, and `BuildResponses` splits a record set across datagrams when it still will
  not fit (RFC 6762 8.3). Measured on the wire: the announcement went from **1629 bytes (IP-fragmented) to
  1023 in a single datagram**, with 12 compressed owner names. NSEC rdata is deliberately left uncompressed,
  since a resolver that does not know the type cannot decompress it.
* **Probing and name-conflict handling** — `MdnsResponder` now runs the RFC 6762 8.1 probe sequence before
  claiming anything, answers no queries until the names are claimed, watches incoming *responses* for
  conflicts (RFC 6762 9), and renames to `Name (2)` / `host-2.local` and re-probes when it finds one.
  Verified live: a second instance started against the running service detected the clash and renamed itself
  once, then both appeared side by side in a browse.
* **`install-service.ps1` hardcoded ports** — the firewall rules and the post-install health check now read
  `HttpPort` / `RawPort` / `RawPortEnabled` from `appsettings.json`, the rule names no longer bake the port
  in, an existing rule's port is updated rather than left stale, the raw-print rule is removed when the raw
  port is disabled, and `uninstall-service.ps1` cleans up both the old and new rule names.
  `add-printer.ps1` derives its default URL from the same config.
* **Test gaps** — 51 to **76 tests**. Added: the multi-request job path (Create-Job / Send-Document /
  Close-Job, which is what iOS, macOS and CUPS use), Cancel-Job, Cancel-My-Jobs, Identify-Printer; DNS name
  compression, pointer validity, datagram splitting, legacy-response TTL capping, probe construction and
  escaped instance names; and the serial pacing arithmetic and ready-line gate, via internal seams that
  keep `System.IO.Ports` out of the tests.
* **Failed documents accumulating** — `KeepFailedDocument` now prunes to the five most recent.

Three real bugs surfaced while closing these, each now covered by a test:

* `Send-Document` on a cancelled job resurrected it, because `Enqueue` resets the state to pending — a client
  that cancelled and then raced a document in would have printed anyway. Now rejected as `not-possible`.
* `DnsWire.ReadName` did not re-escape dots inside a label, so a parsed name could never compare equal to the
  escaped name we generate. Conflict detection would have been blind for any printer whose name contains a dot.
* Comparing raw rdata to detect our own echoed packets broke once rdata names were compressed; `Parse` now
  expands embedded names so a record compares equal however the sender chose to compress it.
