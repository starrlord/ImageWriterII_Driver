# How the encoder drives the ImageWriter II

Source: *Apple ImageWriter II Technical Reference Manual*, chapter 8 (Graphics and Color Printing),
chapter 5 (Page Formatting), chapter 3 (Software Switches), appendix A (Command Summary).
The behaviour was cross-checked with two working drivers (the macOS CUPS driver by Daniele Cattaneo
and `iwiitool`) and the unit tests run every stream through a simulator that implements these rules.

## Graphics model

* The head has nine wires 1/72" apart; graphics use the top eight. One byte = one dot column,
  **bit 0 = top wire, bit 7 = bottom wire** (Figure 8-1). This is the opposite of Epson printers and
  is why the Windows C-Itoh 8510 driver needed `*MirrorRasterByte?: TRUE`.
* Horizontal density is set by the character pitch (Table 8-2):

  | dpi | command | max columns (8") |
  |---|---|---|
  | 72 | ESC n | 576 |
  | 80 | ESC N | 640 |
  | 96 | ESC E | 768 |
  | 107 | ESC e | 856 |
  | 120 | ESC q | 960 |
  | 136 | ESC Q | 1088 |
  | 144 | ESC p | 1152 |
  | 160 | ESC P | 1280 |

* Vertical density: 72 dpi with one pass (ESC T 16 = 16/144" feed per band). 144 dpi with two passes:
  print the even rows, feed 1/144" (ESC T 01, LF), print the odd rows, feed 15/144" (chapter 8 "Dot Spacing").
  1/144" is the smallest paper step, so 144 dpi is the vertical maximum.
* `ESC G nnnn` / `ESC g nnn` (nnn groups of 8 bytes, "somewhat faster") send literal columns.
  `ESC V nnnn c` repeats one column pattern. `ESC F nnnn` places the head at dot column nnnn from the
  left margin (must be followed by data and used in ascending order on a line). Graphics commands
  switch the printer to 8-bit mode automatically.
* Colour: `ESC K n` with 0 black, 1 yellow, 2 magenta, 3 cyan (4-6 are printer-mixed orange, green,
  purple). "The most efficient and fastest way to print colour patterns is to print an entire line of
  graphics in one colour, followed by a carriage return with no line feed, then an entire line in the
  next colour" - print yellow first to avoid contaminating the yellow band.

## Job stream

```
ESC $                 standard character set
SI  ESC "  ESC Y  ESC W  ESC z     double-width, bold, underline, half-height, super/subscript off
ESC Z 0xA0 0x20       open A-8 (no LF after CR), A-6 (no LF when line full), B-6 (8-bit data)
ESC D 0x40 0x04       close A-7 (CR, LF and FF all print), B-3 (perforation skip OFF)
ESC l 0               CR inserted before LF/FF (default)
ESC f                 forward feed
ESC L 000             left margin 0
ESC > | ESC <         unidirectional (default) | bidirectional
ESC K 0               black (only when a colour ribbon is present)
ESC v                 current position = top of form
--- per page ---
ESC H nnnn            page length in 1/144" (media height in points x 2)
ESC x                 pitch for the page's horizontal dpi
--- per 1/9" band (8 rows at 72 dpi, 16 rows at 144 dpi) ---
blank band:           accumulate 16/144" of feed (sent later as ESC T nn + LF, max 99 per LF)
otherwise, per pass, per colour:
   [ESC K n]  [ESC F nnnn]  ESC g/G ... | ESC V nnnn c ...  CR
between the two passes: ESC T 01 LF
after the band:        +15/144" (two passes) or +16/144" (one pass) of pending feed
--- page end ---
FF                    to next top of form
--- job end ---
ESC K 0  ESC A  ESC <
```

No `ESC c` (software reset) is used: the manual warns it takes up to three seconds during which data
is lost. After a cancelled job the spooler does send ESC c, waits 3.5 s and sends FF, because the
printer may be waiting for the rest of a graphics command.

## Compression heuristics

* Trailing blank columns are never sent.
* A run of >= 8 blank columns becomes `ESC F` to the column after the run (6 bytes, and the head
  skips instead of "printing" nothing).
* A run of >= 8 identical non-blank columns becomes `ESC V` (7 bytes).
* Literal runs use `ESC g` when their length is a multiple of eight.
* Blank bands are skipped with a single accumulated feed; a blank page is just `FF`.

At 9600 baud (~960 bytes/s) a full-coverage 144x144 letter page is about 230 KB, i.e. four minutes
of serial transfer, which is also roughly the printer's mechanical speed. Typical text pages are far
smaller thanks to the skips.

## Geometry

Raster pixel (0,0) is the top-left corner of the sheet (PWG rasters cover the whole medium at the
requested resolution). Column 0 of the printer sits `LeftEdgeOffsetInches` (0.25") from the sheet's
left edge, so that many raster columns are dropped; anything beyond 8" of head travel is clipped.
Vertically the top-of-form is the sheet's top edge, so leading blank rows simply become feed.

## Halftoning

Windows renders `sgray_8`/`srgb_8` (8-bit) PWG raster when offered, so the service dithers itself
(Floyd-Steinberg, serpentine) after applying a dot-gain curve: the printer's dots are much larger than
the 1/144" grid, so mid-tones are lightened with `coverage^Gamma` (default 1.8). If a client sends
`black_1` it is already bilevel and printed as-is. Colour pages are separated with full grey-component
replacement so neutral tones use only the black band.
