namespace ImageWriterII.Ipp.Protocol;

/// <summary>IPP tags (RFC 8010 section 3.5).</summary>
public enum IppTag : byte
{
    // delimiter (group) tags
    OperationAttributes = 0x01,
    JobAttributes = 0x02,
    EndOfAttributes = 0x03,
    PrinterAttributes = 0x04,
    UnsupportedAttributes = 0x05,
    SubscriptionAttributes = 0x06,
    EventNotificationAttributes = 0x07,
    ResourceAttributes = 0x08,
    DocumentAttributes = 0x09,
    SystemAttributes = 0x0A,

    // out-of-band value tags
    Unsupported = 0x10,
    Default = 0x11,
    Unknown = 0x12,
    NoValue = 0x13,
    NotSettable = 0x15,
    DeleteAttribute = 0x16,
    AdminDefine = 0x17,

    // integer types
    Integer = 0x21,
    Boolean = 0x22,
    Enum = 0x23,

    // octet string types
    OctetString = 0x30,
    DateTime = 0x31,
    Resolution = 0x32,
    RangeOfInteger = 0x33,
    BegCollection = 0x34,
    TextWithLanguage = 0x35,
    NameWithLanguage = 0x36,
    EndCollection = 0x37,

    // character string types
    TextWithoutLanguage = 0x41,
    NameWithoutLanguage = 0x42,
    Keyword = 0x44,
    Uri = 0x45,
    UriScheme = 0x46,
    Charset = 0x47,
    NaturalLanguage = 0x48,
    MimeMediaType = 0x49,
    MemberAttrName = 0x4A
}

public enum IppOperation : ushort
{
    PrintJob = 0x0002,
    PrintUri = 0x0003,
    ValidateJob = 0x0004,
    CreateJob = 0x0005,
    SendDocument = 0x0006,
    SendUri = 0x0007,
    CancelJob = 0x0008,
    GetJobAttributes = 0x0009,
    GetJobs = 0x000A,
    GetPrinterAttributes = 0x000B,
    HoldJob = 0x000C,
    ReleaseJob = 0x000D,
    RestartJob = 0x000E,
    PausePrinter = 0x0010,
    ResumePrinter = 0x0011,
    PurgeJobs = 0x0012,
    SetPrinterAttributes = 0x0013,
    SetJobAttributes = 0x0014,
    GetPrinterSupportedValues = 0x0015,
    CancelCurrentJob = 0x0035,
    CancelJobs = 0x0038,
    CancelMyJobs = 0x0039,
    CloseJob = 0x003B,
    IdentifyPrinter = 0x003C,
    CupsGetDefault = 0x4001,
    CupsGetPrinters = 0x4002
}

public enum IppStatus : ushort
{
    SuccessfulOk = 0x0000,
    SuccessfulOkIgnoredOrSubstitutedAttributes = 0x0001,
    SuccessfulOkConflictingAttributes = 0x0002,
    ClientErrorBadRequest = 0x0400,
    ClientErrorForbidden = 0x0401,
    ClientErrorNotAuthenticated = 0x0402,
    ClientErrorNotAuthorized = 0x0403,
    ClientErrorNotPossible = 0x0404,
    ClientErrorTimeout = 0x0405,
    ClientErrorNotFound = 0x0406,
    ClientErrorGone = 0x0407,
    ClientErrorRequestEntityTooLarge = 0x0408,
    ClientErrorRequestValueTooLong = 0x0409,
    ClientErrorDocumentFormatNotSupported = 0x040A,
    ClientErrorAttributesOrValuesNotSupported = 0x040B,
    ClientErrorUriSchemeNotSupported = 0x040C,
    ClientErrorCharsetNotSupported = 0x040D,
    ClientErrorConflictingAttributes = 0x040E,
    ClientErrorCompressionNotSupported = 0x040F,
    ClientErrorCompressionError = 0x0410,
    ClientErrorDocumentFormatError = 0x0411,
    ClientErrorDocumentAccessError = 0x0412,
    ClientErrorAttributesNotSettable = 0x0413,
    ClientErrorNotFetchable = 0x0420,
    ServerErrorInternalError = 0x0500,
    ServerErrorOperationNotSupported = 0x0501,
    ServerErrorServiceUnavailable = 0x0502,
    ServerErrorVersionNotSupported = 0x0503,
    ServerErrorDeviceError = 0x0504,
    ServerErrorTemporaryError = 0x0505,
    ServerErrorNotAcceptingJobs = 0x0506,
    ServerErrorBusy = 0x0507,
    ServerErrorJobCanceled = 0x0508,
    ServerErrorMultipleDocumentJobsNotSupported = 0x0509
}

public enum IppJobState
{
    Pending = 3,
    PendingHeld = 4,
    Processing = 5,
    ProcessingStopped = 6,
    Canceled = 7,
    Aborted = 8,
    Completed = 9
}

public enum IppPrinterState
{
    Idle = 3,
    Processing = 4,
    Stopped = 5
}

public readonly record struct IppResolution(int X, int Y, byte Units = 3)
{
    public override string ToString() => Units == 4 ? $"{X}x{Y}dpcm" : $"{X}x{Y}dpi";
}

public readonly record struct IppRange(int Lower, int Upper);

public sealed record IppLocalizedString(string Language, string Text);

public sealed class IppException : Exception
{
    public IppException(IppStatus status, string message) : base(message) => Status = status;
    public IppStatus Status { get; }
}
