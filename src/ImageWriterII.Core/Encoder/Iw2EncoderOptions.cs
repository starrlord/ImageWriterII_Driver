using ImageWriterII.Core.Printer;

namespace ImageWriterII.Core.Encoder;

public enum ColorStrategy
{
    /// <summary>Print every colour of a 1/9" band before feeding (fast, keeps registration exact).</summary>
    PerBand,
    /// <summary>Print the whole page in yellow, reverse-feed to the top, then magenta, cyan and black (tractor paper only).</summary>
    PerPage
}

public sealed class Iw2EncoderOptions
{
    /// <summary>Print graphics in both head directions. Faster; the manual says alignment is excellent, but unidirectional is safest for 144 dpi interleaving.</summary>
    public bool Bidirectional { get; set; }

    /// <summary>Distance from the left edge of the sheet to the print head's leftmost dot column (column 0).</summary>
    public double LeftEdgeOffsetInches { get; set; } = 0.25;

    /// <summary>Additional forward feed applied before printing the first band, for sheets whose top edge sits above the head at top-of-form.</summary>
    public double TopEdgeOffsetInches { get; set; } = 0.0;

    /// <summary>Use ESC F to jump over blank stretches instead of printing zero columns.</summary>
    public bool UseHeadPositioning { get; set; } = true;

    /// <summary>Minimum run of identical columns worth an ESC V repeat command (7 bytes).</summary>
    public int MinRepeatRun { get; set; } = 8;

    /// <summary>Minimum run of blank columns worth an ESC F jump (6 bytes).</summary>
    public int MinSkipRun { get; set; } = 8;

    /// <summary>Use ESC g (eight-byte groups) when the literal length allows; the manual says it is faster.</summary>
    public bool PreferGroupedGraphics { get; set; } = true;

    /// <summary>A four-colour ribbon is installed; colour pages are printed in Y, M, C, K passes.</summary>
    public bool ColorRibbon { get; set; }

    public ColorStrategy ColorStrategy { get; set; } = ColorStrategy.PerBand;

    /// <summary>Send ESC v at job start so wherever the paper currently sits becomes the top of form.</summary>
    public bool SetTopOfFormAtJobStart { get; set; } = true;

    /// <summary>Send a form feed after the last page of the job (always true for pages; kept for raw jobs).</summary>
    public bool FormFeedAfterPage { get; set; } = true;

    /// <summary>
    /// Distance from the print head to the printer's tear edge, for tractor paper that gets torn off after
    /// every job. When set, each job winds the paper back this far before claiming top of form and runs it
    /// forward again afterwards, so the perforation ends up at the tear edge ready to tear.
    ///
    /// Deliberately symmetric and stateless: loading fresh paper and tearing a sheet off both leave the top
    /// of the next sheet sitting at the tear edge, so every job can assume that same starting position and
    /// no state has to survive between jobs or across a service restart. 0 disables it.
    /// </summary>
    public double TearOffInches { get; set; } = 0.0;
}
