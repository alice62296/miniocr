using System.Text;

namespace MiniOcr.Services;

/// <summary>
/// Hand-rolled protobuf for the WeChat OCR messages used by swigger/wechat-ocr
/// (<c>pb/ocr_wx3.proto</c>, <c>pb/ocr_wx4.proto</c>, <c>pb/ocr_common.proto</c>).
/// Small enough that Native AOT does not need Google.Protobuf.
/// </summary>
public static class WeChatProtobuf
{
    public const int MethodPush = 1;
    public const uint Wx3Push = 1;
    public const uint Wx4Handshake = 10001;
    public const uint Wx4Request = 10010;
    public const uint Wx4Response = 10011;

    public static byte[] EncodeRequest(WeChatOcrKind kind, ulong taskId, string absoluteImagePath)
    {
        if (taskId is < 2 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(taskId), "task_id must be in [2, 2147483647].");

        return kind switch
        {
            WeChatOcrKind.Wx3 => EncodeWx3Request(taskId, absoluteImagePath),
            WeChatOcrKind.Wx4 => EncodeWx4Request(taskId, absoluteImagePath),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported WeChat OCR kind."),
        };
    }

    public static byte[] EncodeWx3Request(ulong taskId, string absoluteImagePath)
    {
        var input = new ProtoWriter();
        input.String(1, absoluteImagePath);

        var req = new ProtoWriter();
        req.VarintField(1, 0); // type 0 = do OCR
        req.VarintField(2, taskId);
        req.Message(3, input);
        return req.ToArray();
    }

    public static byte[] EncodeWx4Request(ulong taskId, string absoluteImagePath)
    {
        // ReqType { t1=true, t2=true, t3=false } — upstream sets all three, including false.
        var rt = new ProtoWriter();
        rt.BoolField(1, true);
        rt.BoolField(2, true);
        rt.BoolField(3, false);

        var req = new ProtoWriter();
        req.VarintField(1, taskId);
        req.String(2, absoluteImagePath);
        req.Message(6, rt);
        return req.ToArray();
    }

    public static WeChatPush ParsePush(WeChatOcrKind kind, uint requestId, ReadOnlySpan<byte> payload)
    {
        if (kind == WeChatOcrKind.Wx4)
        {
            if (requestId == Wx4Handshake)
            {
                bool supported = ReadBoolField(payload, fieldNumber: 1);
                return new WeChatPush.Handshake(supported, supported ? 0 : -1);
            }

            if (requestId == Wx4Response)
                return ParseWx4Response(payload);
            return WeChatPush.Ignored.Instance;
        }

        if (kind == WeChatOcrKind.Wx3 && requestId == Wx3Push)
            return ParseWx3Push(payload);

        return WeChatPush.Ignored.Instance;
    }

    public static string JoinLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return "";
        var sb = new StringBuilder();
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line.Trim());
        }

        return sb.ToString();
    }

    private static WeChatPush ParseWx3Push(ReadOnlySpan<byte> payload)
    {
        int type = 0;
        ulong taskId = 0;
        int err = 0;
        bool sawErr = false;
        List<string>? lines = null;
        int width = 0;
        int height = 0;

        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 0:
                    type = reader.ReadVarintInt32();
                    break;
                case 2 when wire == 0:
                    taskId = reader.ReadVarint();
                    break;
                case 3 when wire == 0:
                    err = reader.ReadVarintInt32();
                    sawErr = true;
                    break;
                case 4 when wire == 2:
                    ReadWx3Output(reader.ReadLengthDelimited(), out lines, out width, out height);
                    break;
                default:
                    if (!reader.Skip(wire))
                        return WeChatPush.Ignored.Instance;
                    break;
            }
        }

        // type 1 = init callback (task_id is typically 1).
        if (type == 1)
            return new WeChatPush.Handshake(sawErr ? err == 0 : true, err);

        return new WeChatPush.Result(taskId, err, width, height, lines ?? []);
    }

    private static void ReadWx3Output(ReadOnlySpan<byte> payload, out List<string> lines, out int width, out int height)
    {
        lines = [];
        width = 0;
        height = 0;
        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    string text = ReadLineText(reader.ReadLengthDelimited());
                    if (text.Length > 0)
                        lines.Add(text);
                    break;
                case 2 when wire == 0:
                    width = reader.ReadVarintInt32();
                    break;
                case 3 when wire == 0:
                    height = reader.ReadVarintInt32();
                    break;
                default:
                    if (!reader.Skip(wire))
                        return;
                    break;
            }
        }
    }

    private static WeChatPush ParseWx4Response(ReadOnlySpan<byte> payload)
    {
        ulong taskId = 0;
        int err = 0;
        List<string> lines = [];
        int width = 0;
        int height = 0;

        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 0:
                    taskId = reader.ReadVarint();
                    break;
                case 2 when wire == 0:
                    err = reader.ReadVarintInt32();
                    break;
                case 3 when wire == 2:
                    ReadWx4ResultInfo(reader.ReadLengthDelimited(), lines, ref width, ref height);
                    break;
                default:
                    if (!reader.Skip(wire))
                        break;
                    break;
            }
        }

        return new WeChatPush.Result(taskId, err, width, height, lines);
    }

    private static void ReadWx4ResultInfo(ReadOnlySpan<byte> payload, List<string> lines, ref int width, ref int height)
    {
        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out int field, out int wire))
        {
            switch (field)
            {
                case 3 when wire == 2:
                    string text = ReadLineText(reader.ReadLengthDelimited());
                    if (text.Length > 0)
                        lines.Add(text);
                    break;
                case 4 when wire == 0:
                    width = reader.ReadVarintInt32();
                    break;
                case 5 when wire == 0:
                    height = reader.ReadVarintInt32();
                    break;
                default:
                    if (!reader.Skip(wire))
                        return;
                    break;
            }
        }
    }

    /// <summary>
    /// <c>OCRResultLine.text</c> is field 2. If it is empty, concatenate
    /// <c>blocks</c> (field 4) character strings.
    /// </summary>
    private static string ReadLineText(ReadOnlySpan<byte> line)
    {
        string text = "";
        var chars = new StringBuilder();
        var reader = new ProtoReader(line);
        while (reader.TryReadTag(out int field, out int wire))
        {
            if (field == 2 && wire == 2)
            {
                text = reader.ReadString();
            }
            else if (field == 4 && wire == 2)
            {
                string piece = ReadCharText(reader.ReadLengthDelimited());
                if (piece.Length > 0)
                    chars.Append(piece);
            }
            else if (!reader.Skip(wire))
            {
                break;
            }
        }

        if (text.Length > 0)
            return text;
        return chars.ToString();
    }

    private static string ReadCharText(ReadOnlySpan<byte> block)
    {
        var reader = new ProtoReader(block);
        while (reader.TryReadTag(out int field, out int wire))
        {
            if (field == 2 && wire == 2)
                return reader.ReadString();
            if (!reader.Skip(wire))
                break;
        }

        return "";
    }

    private static bool ReadBoolField(ReadOnlySpan<byte> payload, int fieldNumber)
    {
        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out int field, out int wire))
        {
            if (field == fieldNumber && wire == 0)
                return reader.ReadVarint() != 0;
            if (!reader.Skip(wire))
                break;
        }

        return false;
    }
}

public abstract record WeChatPush
{
    private WeChatPush()
    {
    }

    public sealed record Handshake(bool Ok, int ErrCode) : WeChatPush;

    public sealed record Result(ulong TaskId, int ErrCode, int Width, int Height, IReadOnlyList<string> Lines) : WeChatPush;

    public sealed record Ignored : WeChatPush
    {
        public static readonly Ignored Instance = new();
    }
}

internal ref struct ProtoReader
{
    private readonly ReadOnlySpan<byte> _buf;
    private int _pos;

    public ProtoReader(ReadOnlySpan<byte> buf)
    {
        _buf = buf;
        _pos = 0;
    }

    public bool TryReadTag(out int field, out int wire)
    {
        field = 0;
        wire = 0;
        if (_pos >= _buf.Length)
            return false;
        if (!TryReadVarint(out ulong tag))
            return false;
        wire = (int)(tag & 7);
        field = (int)(tag >> 3);
        return field > 0;
    }

    public ulong ReadVarint()
    {
        if (!TryReadVarint(out ulong value))
            throw new InvalidOperationException("Truncated protobuf varint.");
        return value;
    }

    public int ReadVarintInt32() => unchecked((int)ReadVarint());

    public ReadOnlySpan<byte> ReadLengthDelimited()
    {
        int len = checked((int)ReadVarint());
        if (len < 0 || _pos + len > _buf.Length)
            throw new InvalidOperationException("Truncated protobuf length-delimited field.");
        ReadOnlySpan<byte> slice = _buf.Slice(_pos, len);
        _pos += len;
        return slice;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadLengthDelimited());

    public bool Skip(int wire)
    {
        switch (wire)
        {
            case 0:
                return TryReadVarint(out _);
            case 1:
                return SkipRaw(8);
            case 2:
                if (!TryReadVarint(out ulong len))
                    return false;
                return len <= int.MaxValue && SkipRaw((int)len);
            case 5:
                return SkipRaw(4);
            default:
                return false;
        }
    }

    private bool TryReadVarint(out ulong value)
    {
        value = 0;
        int shift = 0;
        while (_pos < _buf.Length && shift < 64)
        {
            byte b = _buf[_pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return true;
            shift += 7;
        }

        return false;
    }

    private bool SkipRaw(int n)
    {
        if (n < 0 || _pos > _buf.Length - n)
            return false;
        _pos += n;
        return true;
    }
}

internal sealed class ProtoWriter
{
    private readonly List<byte> _buf = [];

    public byte[] ToArray() => _buf.ToArray();

    public void VarintField(int field, ulong value)
    {
        Key(field, 0);
        Varint(value);
    }

    public void BoolField(int field, bool value)
    {
        Key(field, 0);
        _buf.Add(value ? (byte)1 : (byte)0);
    }

    public void String(int field, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        Key(field, 2);
        Varint((ulong)utf8.Length);
        _buf.AddRange(utf8);
    }

    public void Message(int field, ProtoWriter nested)
    {
        byte[] payload = nested.ToArray();
        Key(field, 2);
        Varint((ulong)payload.Length);
        _buf.AddRange(payload);
    }

    private void Key(int field, int wire) => Varint((ulong)((field << 3) | wire));

    private void Varint(ulong value)
    {
        while (true)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
                b |= 0x80;
            _buf.Add(b);
            if (value == 0)
                break;
        }
    }
}
