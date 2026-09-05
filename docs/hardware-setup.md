# Hardware setup: ImageWriter II to a Windows PC

Everything here is taken from the *Apple ImageWriter II Technical Reference Manual*
(chapter 2 "DIP switches", appendix A, appendix E "Interface Specifications") and verified
against the service's serial-port defaults.

## Serial parameters

The printer is permanently 8 data bits, 1 start bit, 1 stop bit, no parity. Only speed and
handshake are selectable:

| DIP switch | Open (up) | Closed (down) | Service setting |
|---|---|---|---|
| SW2-1, SW2-2 | 300 / 2400 baud | 1200 / 9600 baud (both closed = **9600**, factory) | `Serial.BaudRate` |
| SW2-3 | **Hardware handshake (DTR)**, factory | XON/XOFF | `Serial.Handshake` = `Auto` (detects CTS/DSR/DCD), `RequestToSend`, `DataSetReady`, or `XOnXOff` |
| SW2-4 | Option card disabled | Option card (32K buffer / AppleTalk) | n/a |
| SW2-5, SW2-6 | Hammer-fire timing, factory set | do not change | n/a |

Baud-rate table (SW2-1 / SW2-2): open/open 300, closed/open 1200, open/closed 2400, closed/closed 9600.

SW1 (character set, form length, perforation skip, pitch, auto-LF) does not matter for graphics
printing: the service overrides every SW1 function with software switches at the start of each job
(no auto line feed after CR, perforation skip off, CR/LF/FF all cause printing, 8-bit data).

## Cable: Mini-DIN-8 to RS-232

The printer speaks RS-422 levels but works fine with an RS-232 port when wired as the manual's
Table E-3 describes (RxD+ grounded, TxD+ unused):

| Mini-DIN-8 pin | Signal (printer side) | DB-9 (PC) | DB-25 (PC) |
|---|---|---|---|
| 1 | DSR in (unused by the printer) | 4 DTR | 20 DTR |
| 2 | **DTR out** (busy / ready) | **8 CTS** (or 6 DSR; the service detects either) | 5 CTS (or 6 DSR) |
| 3 | RxD- (data *to* printer) | 3 TxD | 2 TxD |
| 4 | GND | 5 GND | 7 GND |
| 5 | TxD- (data *from* printer: self-ID, XON/XOFF) | 2 RxD | 3 RxD |
| 6 | NC | | |
| 7 | NC (RxD+ must be tied to pin 4/8 ground) | | |
| 8 | GND (tie to 4) | 5 GND | 7 GND |

A "Mac to PC" or "ImageWriter to PC" cable from the usual vintage-Mac shops is wired like this.
Things to check:

* **Pin 2 (DTR) is the printer's ready/busy signal and must reach the PC on some control line.**
  Apple-style DIN-8 to DB-25 cables usually deliver it on DSR and/or DCD (pins 6/8) rather than CTS,
  and "ImageWriter I" cables are wired as null-modems because that printer was a DTE. Run
  `iwprint status` with the printer switched on and selected: it shows CTS/DSR/DCD and which setting
  to use. `Handshake: Auto` (the default) picks the asserted line by itself: CTS and DSR are handled by
  the adapter in hardware (the FTDI chip stops within a few bytes, inside the printer's 27-character
  grace period); DCD has no hardware support, so output is gated and paced in software.
* XON/XOFF needs pin 5 (TxD-) wired to the PC's RxD. It also needs the PC to stop within 256
  characters of the XOFF; USB adapters that buffer kilobytes in the driver can overrun the
  printer's 2K buffer, so prefer the DTR line (CTS or DSR). If you must use XON/XOFF and see garbage, set
  `Serial.MaxBytesPerSecond` to ~700 to pace the output.
* The self-ID query (ribbon auto-detection) and `iwprint identify` need pin 5 wired. If the cable
  has no return line, set `Ribbon` to `Black` or `Color` explicitly; nothing else needs it.
* **Telling the two cable types apart.** Mini-DIN-8 to DB-25 "modem" cables (Mac to modem, or the
  generic M/M molded ones) are straight from the printer's point of view: PC TxD reaches the printer's
  RxD through a plain DB-9 to DB-25 "AT modem" adapter. "Mac to ImageWriter I" cables are crossed:
  with the same adapter the PC's TxD lands on the printer's TxD and nothing prints even though the
  ready line looks fine. If `iwprint status` reports ready but `iwprint text` prints nothing, insert
  a DB-25 null-modem adapter (or use the other cable).

## Paper position

* Top-of-form: the service sends ESC v at the start of every job, so wherever the paper sits when
  a job starts becomes the top of the page. Load paper so the print head is at the top edge of the
  sheet (the printer's paper-load position does this for single sheets; with fanfold paper set the
  perforation just above the head).
* Left edge: the head's first dot column is 1/4" from the left edge of letter paper loaded against
  the paper guide. If output is shifted, adjust `Encoder.LeftEdgeOffsetInches` (bigger value moves
  the image left on the sheet). The `iwprint testpage` border makes this easy to measure.
* Page length is set per job with ESC H from the media size, so the SW1-4 (11"/12") switch is irrelevant.

## Ribbons

The four-colour ribbon is detected automatically at service start (ESC ? returns `IW10C`).
Colour jobs print yellow, magenta, cyan, black in that order for every 1/9" band, which is what the
manual recommends to keep the yellow band clean. `Encoder.ColorStrategy = PerPage` prints the whole
page per colour with reverse feeds; only use it with tractor paper (reverse feeding is not reliable
once a cut sheet leaves the pressure roller).
