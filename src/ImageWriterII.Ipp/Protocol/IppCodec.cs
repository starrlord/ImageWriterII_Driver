using System.Buffers.Binary;
using System.Text;

namespace ImageWriterII.Ipp.Protocol;

/// <summary>Binary encoding of IPP messages (RFC 8010).</summary>
public static class IppCodec
{
    private const int MaxAttributeCount = 20000;

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// Reads exactly one IPP message from the stream and leaves the stream positioned at the first byte
    /// after the end-of-attributes tag, i.e. at the start of the document data of a Print-Job request.
    /// </summary>
    public static IppMessage Read(Stream stream)
    {
        var r = new ByteReader(stream);
        var msg = new IppMessage
        {
            VersionMajor = r.U8(),
            VersionMinor = r.U8(),
            OperationOrStatus = r.U16(),
            RequestId = (int)r.U32()
        };

        IppAttributeGroup? group = null;
        IppAttribute? last = null;
        var collectionStack = new Stack<(IppCollection collection, IppAttribute? lastMember)>();
        int attributeCount = 0;

        while (true)
        {
            int tagByte = r.TryU8();
            if (tagByte < 0) throw new IppException(IppStatus.ClientErrorBadRequest, "Unexpected end of IPP message.");
            var tag = (IppTag)tagByte;
            if (tag == IppTag.EndOfAttributes) break;
            if (tagByte < 0x10)
            {
                group = msg.AddGroup(tag);
                last = null;
                collectionStack.Clear();
                continue;
            }
            if (group is null) throw new IppException(IppStatus.ClientErrorBadRequest, "Attribute before any group delimiter.");
            if (++attributeCount > MaxAttributeCount) throw new IppException(IppStatus.ClientErrorRequestEntityTooLarge, "Too many attributes.");

            int nameLen = r.U16();
            string name = nameLen > 0 ? r.Utf8(nameLen) : "";
            int valueLen = r.U16();
            byte[] raw = r.Bytes(valueLen);

            if (collectionStack.Count > 0)
            {
                var (collection, lastMember) = collectionStack.Peek();
                if (tag == IppTag.EndCollection)
                {
                    collectionStack.Pop();
                    continue;
                }
                if (tag == IppTag.MemberAttrName)
                {
                    var member = new IppAttribute(Encoding.UTF8.GetString(raw));
                    collection.Members.Add(member);
                    collectionStack.Pop();
                    collectionStack.Push((collection, member));
                    continue;
                }
                if (lastMember is null) throw new IppException(IppStatus.ClientErrorBadRequest, "Collection value without member name.");
                var v = DecodeValue(tag, raw);
                lastMember.Values.Add(v);
                if (tag == IppTag.BegCollection) collectionStack.Push(((IppCollection)v.Data!, null));
                continue;
            }

            if (nameLen == 0)
            {
                // additional value for the previous attribute
                if (last is null) throw new IppException(IppStatus.ClientErrorBadRequest, "Additional value without attribute.");
                if (tag == IppTag.EndCollection) continue;
                var v = DecodeValue(tag, raw);
                last.Values.Add(v);
                if (tag == IppTag.BegCollection) collectionStack.Push(((IppCollection)v.Data!, null));
            }
            else
            {
                var v = DecodeValue(tag, raw);
                last = new IppAttribute(name, v);
                group.Attributes.Add(last);
                if (tag == IppTag.BegCollection) collectionStack.Push(((IppCollection)v.Data!, null));
            }
        }
        return msg;
    }

    private static IppValue DecodeValue(IppTag tag, byte[] raw)
    {
        switch (tag)
        {
            case IppTag.Integer:
            case IppTag.Enum:
                if (raw.Length != 4) throw new IppException(IppStatus.ClientErrorBadRequest, $"Bad {tag} length {raw.Length}.");
                return new IppValue(tag, BinaryPrimitives.ReadInt32BigEndian(raw));
            case IppTag.Boolean:
                if (raw.Length != 1) throw new IppException(IppStatus.ClientErrorBadRequest, "Bad boolean length.");
                return new IppValue(tag, raw[0] != 0);
            case IppTag.Resolution:
                if (raw.Length != 9) throw new IppException(IppStatus.ClientErrorBadRequest, "Bad resolution length.");
                return new IppValue(tag, new IppResolution(BinaryPrimitives.ReadInt32BigEndian(raw), BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(4)), raw[8]));
            case IppTag.RangeOfInteger:
                if (raw.Length != 8) throw new IppException(IppStatus.ClientErrorBadRequest, "Bad range length.");
                return new IppValue(tag, new IppRange(BinaryPrimitives.ReadInt32BigEndian(raw), BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(4))));
            case IppTag.DateTime:
                if (raw.Length != 11) throw new IppException(IppStatus.ClientErrorBadRequest, "Bad dateTime length.");
                return new IppValue(tag, DecodeDateTime(raw));
            case IppTag.BegCollection:
                return new IppValue(tag, new IppCollection());
            case IppTag.TextWithLanguage:
            case IppTag.NameWithLanguage:
                {
                    if (raw.Length < 4) throw new IppException(IppStatus.ClientErrorBadRequest, "Bad localized string.");
                    int ll = BinaryPrimitives.ReadUInt16BigEndian(raw);
                    string lang = Encoding.UTF8.GetString(raw, 2, ll);
                    int tl = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2 + ll));
                    string text = Encoding.UTF8.GetString(raw, 4 + ll, tl);
                    return new IppValue(tag, new IppLocalizedString(lang, text));
                }
            case IppTag.TextWithoutLanguage:
            case IppTag.NameWithoutLanguage:
            case IppTag.Keyword:
            case IppTag.Uri:
            case IppTag.UriScheme:
            case IppTag.Charset:
            case IppTag.NaturalLanguage:
            case IppTag.MimeMediaType:
            case IppTag.MemberAttrName:
                return new IppValue(tag, Encoding.UTF8.GetString(raw));
            case IppTag.OctetString:
                return new IppValue(tag, raw);
            case IppTag.Unsupported:
            case IppTag.Default:
            case IppTag.Unknown:
            case IppTag.NoValue:
            case IppTag.NotSettable:
            case IppTag.DeleteAttribute:
            case IppTag.AdminDefine:
                return new IppValue(tag, null);
            default:
                // unknown syntax: keep the raw bytes so we can echo it back as unsupported
                return new IppValue(tag, raw);
        }
    }

    private static DateTimeOffset DecodeDateTime(byte[] b)
    {
        int year = BinaryPrimitives.ReadUInt16BigEndian(b);
        int month = b[2], day = b[3], hour = b[4], minute = b[5], second = b[6], deci = b[7];
        int sign = b[8] == (byte)'-' ? -1 : 1;
        var offset = new TimeSpan(b[9], b[10], 0) * sign;
        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, second, deci * 100, offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    // ---------------------------------------------------------------- writing

    public static byte[] Write(IppMessage msg)
    {
        var ms = new MemoryStream();
        Write(msg, ms);
        return ms.ToArray();
    }

    public static void Write(IppMessage msg, Stream stream)
    {
        var w = new ByteWriter(stream);
        w.U8(msg.VersionMajor);
        w.U8(msg.VersionMinor);
        w.U16(msg.OperationOrStatus);
        w.U32((uint)msg.RequestId);
        foreach (var g in msg.Groups)
        {
            w.U8((byte)g.Tag);
            foreach (var a in g.Attributes) WriteAttribute(w, a);
        }
        w.U8((byte)IppTag.EndOfAttributes);
        w.Flush();
    }

    private static void WriteAttribute(ByteWriter w, IppAttribute a)
    {
        if (a.Values.Count == 0)
        {
            w.U8((byte)IppTag.Unknown);
            w.Utf8WithLength(a.Name);
            w.U16(0);
            return;
        }
        for (int i = 0; i < a.Values.Count; i++)
            WriteValue(w, i == 0 ? a.Name : "", a.Values[i]);
    }

    private static void WriteValue(ByteWriter w, string name, IppValue v)
    {
        w.U8((byte)v.Tag);
        w.Utf8WithLength(name);
        switch (v.Data)
        {
            case null:
                w.U16(0);
                break;
            case int i:
                w.U16(4);
                w.U32((uint)i);
                break;
            case bool b:
                w.U16(1);
                w.U8((byte)(b ? 1 : 0));
                break;
            case string s:
                w.Utf8WithLength(s);
                break;
            case byte[] bytes:
                w.U16((ushort)bytes.Length);
                w.Bytes(bytes);
                break;
            case IppResolution res:
                w.U16(9);
                w.U32((uint)res.X);
                w.U32((uint)res.Y);
                w.U8(res.Units);
                break;
            case IppRange range:
                w.U16(8);
                w.U32((uint)range.Lower);
                w.U32((uint)range.Upper);
                break;
            case DateTimeOffset dt:
                w.U16(11);
                w.U16((ushort)dt.Year);
                w.U8((byte)dt.Month);
                w.U8((byte)dt.Day);
                w.U8((byte)dt.Hour);
                w.U8((byte)dt.Minute);
                w.U8((byte)dt.Second);
                w.U8((byte)(dt.Millisecond / 100));
                w.U8((byte)(dt.Offset < TimeSpan.Zero ? '-' : '+'));
                w.U8((byte)Math.Abs(dt.Offset.Hours));
                w.U8((byte)Math.Abs(dt.Offset.Minutes));
                break;
            case IppLocalizedString ls:
                {
                    var lang = Encoding.UTF8.GetBytes(ls.Language);
                    var text = Encoding.UTF8.GetBytes(ls.Text);
                    w.U16((ushort)(4 + lang.Length + text.Length));
                    w.U16((ushort)lang.Length);
                    w.Bytes(lang);
                    w.U16((ushort)text.Length);
                    w.Bytes(text);
                    break;
                }
            case IppCollection c:
                w.U16(0); // begCollection has an empty value
                foreach (var m in c.Members)
                {
                    w.U8((byte)IppTag.MemberAttrName);
                    w.U16(0);
                    w.Utf8WithLength(m.Name);
                    foreach (var mv in m.Values) WriteValue(w, "", mv);
                }
                w.U8((byte)IppTag.EndCollection);
                w.U16(0);
                w.U16(0);
                break;
            default:
                throw new InvalidOperationException($"Cannot encode value of type {v.Data.GetType()}.");
        }
    }

    private sealed class ByteReader
    {
        private readonly Stream _s;
        public ByteReader(Stream s) => _s = s;

        public int TryU8() => _s.ReadByte();

        public byte U8()
        {
            int b = _s.ReadByte();
            if (b < 0) throw new IppException(IppStatus.ClientErrorBadRequest, "Unexpected end of IPP message.");
            return (byte)b;
        }

        public ushort U16() => (ushort)((U8() << 8) | U8());

        public uint U32() => (uint)((U8() << 24) | (U8() << 16) | (U8() << 8) | U8());

        public byte[] Bytes(int n)
        {
            if (n > 65535) throw new IppException(IppStatus.ClientErrorBadRequest, "Value too long.");
            var b = new byte[n];
            int total = 0;
            while (total < n)
            {
                int read = _s.Read(b, total, n - total);
                if (read <= 0) throw new IppException(IppStatus.ClientErrorBadRequest, "Unexpected end of IPP message.");
                total += read;
            }
            return b;
        }

        public string Utf8(int n) => Encoding.UTF8.GetString(Bytes(n));
    }

    private sealed class ByteWriter
    {
        private readonly Stream _s;
        private readonly byte[] _buf = new byte[8192];
        private int _n;
        public ByteWriter(Stream s) => _s = s;

        public void U8(byte b)
        {
            if (_n == _buf.Length) Flush();
            _buf[_n++] = b;
        }
        public void U16(ushort v) { U8((byte)(v >> 8)); U8((byte)v); }
        public void U32(uint v) { U8((byte)(v >> 24)); U8((byte)(v >> 16)); U8((byte)(v >> 8)); U8((byte)v); }
        public void Bytes(ReadOnlySpan<byte> b) { foreach (var x in b) U8(x); }
        public void Utf8WithLength(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            if (b.Length > 65535) throw new InvalidOperationException("String too long for IPP.");
            U16((ushort)b.Length);
            Bytes(b);
        }
        public void Flush()
        {
            if (_n > 0) { _s.Write(_buf, 0, _n); _n = 0; }
        }
    }
}
