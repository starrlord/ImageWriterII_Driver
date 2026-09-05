using System.Text;

namespace ImageWriterII.Ipp.Protocol;

/// <summary>A single attribute value with its syntax tag. Data is int, bool, string, byte[], IppResolution, IppRange, IppCollection, DateTimeOffset, IppLocalizedString, or null for out-of-band tags.</summary>
public sealed class IppValue
{
    public IppValue(IppTag tag, object? data)
    {
        Tag = tag;
        Data = data;
    }

    public IppTag Tag { get; }
    public object? Data { get; }

    public int AsInt() => Data is int i ? i : throw new IppException(IppStatus.ClientErrorBadRequest, $"Expected integer, got {Tag}.");
    public bool AsBool() => Data is bool b ? b : throw new IppException(IppStatus.ClientErrorBadRequest, $"Expected boolean, got {Tag}.");
    public string AsString() => Data switch
    {
        string s => s,
        IppLocalizedString l => l.Text,
        byte[] b => Encoding.UTF8.GetString(b),
        _ => Data?.ToString() ?? ""
    };
    public IppCollection AsCollection() => Data as IppCollection ?? throw new IppException(IppStatus.ClientErrorBadRequest, $"Expected collection, got {Tag}.");

    public static IppValue Integer(int v) => new(IppTag.Integer, v);
    public static IppValue Boolean(bool v) => new(IppTag.Boolean, v);
    public static IppValue Enum(int v) => new(IppTag.Enum, v);
    public static IppValue Keyword(string v) => new(IppTag.Keyword, v);
    public static IppValue Name(string v) => new(IppTag.NameWithoutLanguage, v);
    public static IppValue Text(string v) => new(IppTag.TextWithoutLanguage, v);
    public static IppValue Uri(string v) => new(IppTag.Uri, v);
    public static IppValue UriScheme(string v) => new(IppTag.UriScheme, v);
    public static IppValue Charset(string v) => new(IppTag.Charset, v);
    public static IppValue Language(string v) => new(IppTag.NaturalLanguage, v);
    public static IppValue MimeType(string v) => new(IppTag.MimeMediaType, v);
    public static IppValue Octets(byte[] v) => new(IppTag.OctetString, v);
    public static IppValue Resolution(int x, int y) => new(IppTag.Resolution, new IppResolution(x, y));
    public static IppValue Range(int lower, int upper) => new(IppTag.RangeOfInteger, new IppRange(lower, upper));
    public static IppValue Collection(IppCollection c) => new(IppTag.BegCollection, c);
    public static IppValue DateTime(DateTimeOffset dt) => new(IppTag.DateTime, dt);
    public static IppValue NoValue() => new(IppTag.NoValue, null);
    public static IppValue Unknown() => new(IppTag.Unknown, null);
    public static IppValue Unsupported() => new(IppTag.Unsupported, null);

    public override string ToString() => Data switch
    {
        null => Tag.ToString(),
        byte[] b => $"{b.Length} bytes",
        IppCollection c => c.ToString(),
        _ => Data.ToString() ?? ""
    };
}

public sealed class IppAttribute
{
    public IppAttribute(string name, params IppValue[] values)
    {
        Name = name;
        Values = [.. values];
    }

    public IppAttribute(string name, IEnumerable<IppValue> values)
    {
        Name = name;
        Values = [.. values];
    }

    public string Name { get; }
    public List<IppValue> Values { get; }
    public IppValue First => Values.Count > 0 ? Values[0] : throw new IppException(IppStatus.ClientErrorBadRequest, $"Attribute {Name} has no value.");
    public IppTag Tag => Values.Count > 0 ? Values[0].Tag : IppTag.NoValue;

    public override string ToString() => $"{Name}={string.Join(",", Values)}";
}

/// <summary>Member attributes of a collection value (1setOf member attributes).</summary>
public sealed class IppCollection
{
    public List<IppAttribute> Members { get; } = [];

    public IppCollection Add(string name, params IppValue[] values)
    {
        Members.Add(new IppAttribute(name, values));
        return this;
    }

    public IppAttribute? Find(string name) => Members.FirstOrDefault(m => m.Name == name);

    public override string ToString() => "{" + string.Join(" ", Members) + "}";
}

public sealed class IppAttributeGroup
{
    public IppAttributeGroup(IppTag tag) => Tag = tag;

    public IppTag Tag { get; }
    public List<IppAttribute> Attributes { get; } = [];

    public IppAttribute? Find(string name) => Attributes.FirstOrDefault(a => a.Name == name);

    public IppAttributeGroup Add(string name, params IppValue[] values)
    {
        Attributes.Add(new IppAttribute(name, values));
        return this;
    }

    public IppAttributeGroup Add(IppAttribute attribute)
    {
        Attributes.Add(attribute);
        return this;
    }
}

public sealed class IppMessage
{
    public byte VersionMajor { get; set; } = 2;
    public byte VersionMinor { get; set; } = 0;
    /// <summary>Operation id for requests, status code for responses.</summary>
    public ushort OperationOrStatus { get; set; }
    public int RequestId { get; set; }
    public List<IppAttributeGroup> Groups { get; } = [];

    public IppOperation Operation => (IppOperation)OperationOrStatus;
    public IppStatus Status => (IppStatus)OperationOrStatus;

    public IppAttributeGroup AddGroup(IppTag tag)
    {
        var g = new IppAttributeGroup(tag);
        Groups.Add(g);
        return g;
    }

    public IppAttributeGroup? Group(IppTag tag) => Groups.FirstOrDefault(g => g.Tag == tag);

    public IEnumerable<IppAttributeGroup> GroupsOf(IppTag tag) => Groups.Where(g => g.Tag == tag);

    public IppAttribute? Find(IppTag group, string name) => Group(group)?.Find(name);

    public IppAttribute? FindAny(string name) => Groups.Select(g => g.Find(name)).FirstOrDefault(a => a is not null);

    public string? GetString(IppTag group, string name) => Find(group, name)?.First.AsString();

    public int? GetInt(IppTag group, string name)
    {
        var a = Find(group, name);
        return a?.First.Data is int i ? i : null;
    }

    public bool? GetBool(IppTag group, string name)
    {
        var a = Find(group, name);
        return a?.First.Data is bool b ? b : null;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"IPP/{VersionMajor}.{VersionMinor} 0x{OperationOrStatus:X4} request-id {RequestId}");
        foreach (var g in Groups)
        {
            sb.Append($"\n  [{g.Tag}]");
            foreach (var a in g.Attributes) sb.Append($"\n    {a}");
        }
        return sb.ToString();
    }
}
