using ImageWriterII.Ipp.Protocol;

namespace ImageWriterII.Ipp.Server;

public enum JobDocumentKind
{
    None,
    PwgRaster,
    AppleRaster,
    Text,
    Raw
}

/// <summary>A queued, printing or finished job. Mutations happen under <see cref="Sync"/>.</summary>
public sealed class PrintJob
{
    public object Sync { get; } = new();

    public required int Id { get; init; }
    public required string Name { get; set; }
    public required string UserName { get; init; }
    public string DocumentName { get; set; } = "";
    public string Format { get; set; } = "application/octet-stream";
    public string Compression { get; set; } = "none";
    public string Source { get; init; } = "ipp";

    /// <summary>Spool file holding the document; <see cref="DocumentOffset"/> bytes of IPP header precede it.</summary>
    public string? SpoolPath { get; set; }
    public long DocumentOffset { get; set; }
    public long DocumentBytes { get; set; }
    public JobDocumentKind Kind { get; set; } = JobDocumentKind.None;
    public bool DocumentsComplete { get; set; }

    public IppJobState State { get; set; } = IppJobState.Pending;
    public List<string> StateReasons { get; } = ["job-incoming"];
    public string StateMessage { get; set; } = "";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? ProcessingAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public int Copies { get; set; } = 1;
    public bool Collate { get; set; } = true;
    public string Media { get; set; } = "";
    public IppResolution? Resolution { get; set; }
    public int PrintQuality { get; set; }
    public string ColorMode { get; set; } = "";
    public int Impressions { get; set; }
    public int ImpressionsCompleted { get; set; }
    public int BandsDone { get; set; }
    public int BandsTotal { get; set; }

    /// <summary>Job template attributes as received, echoed back by Get-Job-Attributes.</summary>
    public List<IppAttribute> TemplateAttributes { get; } = [];

    public CancellationTokenSource Cancellation { get; } = new();

    public bool IsFinished => State is IppJobState.Completed or IppJobState.Canceled or IppJobState.Aborted;

    public void SetState(IppJobState state, params string[] reasons)
    {
        lock (Sync)
        {
            State = state;
            StateReasons.Clear();
            StateReasons.AddRange(reasons.Length == 0 ? ["none"] : reasons);
            if (state == IppJobState.Processing && ProcessingAt is null) ProcessingAt = DateTimeOffset.Now;
            if (IsFinished && CompletedAt is null) CompletedAt = DateTimeOffset.Now;
        }
    }

    public override string ToString() => $"job {Id} '{Name}' ({Format}, {DocumentBytes} bytes) {State}";
}
