// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tekium.Embedded;

/// <summary>
/// The binary form of a JSON document. It is file format: a <c>jsonb</c> column stores these bytes.
/// </summary>
/// <remarks>
/// <para>A document is a version byte (1) and a value. A value starts with its kind:</para>
/// <list type="bullet">
/// <item>0 null, 1 false, 2 true.</item>
/// <item>3 number: a sign byte (0 or 1), the count of its digits and the digits in ASCII (no leading or
/// trailing zeros; zero is "0"), and the power of ten, four bytes. So every number has one form and keeps
/// all its digits.</item>
/// <item>4 string: its length in UTF-8 bytes and the bytes, unescaped.</item>
/// <item>5 array: the bytes that follow (four), the count of elements (four), where each element starts
/// (four each, from the start of the elements; the first is 0), and the elements.</item>
/// <item>6 object: the bytes that follow (four), the count of members (four), where the key and the value
/// of each start (four and four, from the start of the data), and the data: each key as a string without
/// its kind byte, then each value. The keys are sorted by their bytes and not repeated.</item>
/// </list>
/// <para>Counts and lengths of digits and strings are unsigned LEB128, in their shortest form. Every
/// four-byte number is little-endian. <see cref="Verify"/> checks all of it, so a document that passed it
/// is read without further checks and equal documents have equal bytes.</para>
/// </remarks>
internal static class JsonBinary
{
    public const byte Version = 1;

    private const byte NullTag = 0;
    private const byte FalseTag = 1;
    private const byte TrueTag = 2;
    private const byte NumberTag = 3;
    private const byte StringTag = 4;
    private const byte ArrayTag = 5;
    private const byte ObjectTag = 6;

    private static readonly JsonReaderOptions Strict = new() { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = JsonText.MaxDepth };

    public static byte[] FromText(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, Strict);
        reader.Read();
        var output = new ArrayBufferWriter<byte>(utf8.Length + 8);
        output.Write([Version]);
        Encode(ref reader, output);
        return output.WrittenSpan.ToArray();
    }

    public static JsonValueKind Kind(ReadOnlySpan<byte> bytes, int position) => bytes[position] switch
    {
        NullTag => JsonValueKind.Null,
        FalseTag => JsonValueKind.False,
        TrueTag => JsonValueKind.True,
        NumberTag => JsonValueKind.Number,
        StringTag => JsonValueKind.String,
        ArrayTag => JsonValueKind.Array,
        _ => JsonValueKind.Object,
    };

    public static string String(ReadOnlySpan<byte> bytes, int position)
    {
        position++;
        var length = (int)ReadCount(bytes, ref position);
        return TkText.Utf8.GetString(bytes.Slice(position, length));
    }

    public static JsonNumber Number(ReadOnlySpan<byte> bytes, int position)
    {
        position++;
        var negative = bytes[position++] == 1;
        var count = (int)ReadCount(bytes, ref position);
        var digits = Encoding.ASCII.GetString(bytes.Slice(position, count));
        position += count;
        return new JsonNumber(negative, digits, BinaryPrimitives.ReadInt32LittleEndian(bytes[position..]));
    }

    /// <summary>Where the value at a path is: its start and length. Length 0 if there is none.</summary>
    public static (int Start, int Length) Find(ReadOnlySpan<byte> bytes, TkJsonPath path)
    {
        var position = 1;
        Span<byte> scratch = stackalloc byte[256];
        foreach (var step in path.Steps)
        {
            var tag = bytes[position];
            if (step.Name is { } name)
            {
                if (tag != ObjectTag)
                {
                    return default;
                }

                var count = Int(bytes, position + 5);
                var table = position + 9;
                var data = table + (count * 8);
                var key = Encoding.UTF8.GetByteCount(name) <= scratch.Length ? scratch[..Encoding.UTF8.GetBytes(name, scratch)] : Encoding.UTF8.GetBytes(name);
                int low = 0, high = count - 1, found = -1;
                while (low <= high)
                {
                    var middle = (low + high) >>> 1;
                    var at = data + Int(bytes, table + (middle * 8));
                    var length = (int)ReadCount(bytes, ref at);
                    var order = bytes.Slice(at, length).SequenceCompareTo(key);
                    if (order == 0)
                    {
                        found = middle;
                        break;
                    }

                    (low, high) = order < 0 ? (middle + 1, high) : (low, middle - 1);
                }

                if (found < 0)
                {
                    return default;
                }

                position = data + Int(bytes, table + (found * 8) + 4);
            }
            else
            {
                if (tag != ArrayTag)
                {
                    return default;
                }

                var count = Int(bytes, position + 5);
                var index = step.IsFromEnd ? count - step.Index : step.Index;
                if (index < 0 || index >= count)
                {
                    return default;
                }

                position = position + 9 + (count * 4) + Int(bytes, position + 9 + (index * 4));
            }
        }

        return (position, Length(bytes, position));
    }

    /// <summary>The canonical text of a document: no spaces, keys in their order, each number written once.</summary>
    public static byte[] ToText(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder();
        Write(bytes, 1, text);
        return TkText.Utf8.GetBytes(text.ToString());
    }

    /// <summary>Checks a whole document. Throws <see cref="InvalidDataException"/> if anything about it is not as written here.</summary>
    public static void Verify(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes[0] != Version)
        {
            throw Damaged("it does not start as a document of this version");
        }

        if (VerifyValue(bytes, 1, 0) != bytes.Length)
        {
            throw Damaged("there are bytes after its value");
        }
    }

    private static void Encode(ref Utf8JsonReader reader, ArrayBufferWriter<byte> output)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                output.Write([NullTag]);
                break;
            case JsonTokenType.False:
                output.Write([FalseTag]);
                break;
            case JsonTokenType.True:
                output.Write([TrueTag]);
                break;
            case JsonTokenType.Number:
                var number = JsonNumber.Parse(reader.ValueSpan);
                output.Write([NumberTag, number.Negative ? (byte)1 : (byte)0]);
                WriteCount(output, (uint)number.Digits.Length);
                output.Write(Encoding.ASCII.GetBytes(number.Digits));
                BinaryPrimitives.WriteInt32LittleEndian(output.GetSpan(4), number.Exponent);
                output.Advance(4);
                break;
            case JsonTokenType.String:
                output.Write([StringTag]);
                WriteString(output, reader.GetString()!);
                break;
            case JsonTokenType.StartArray:
                var elements = new List<byte[]>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var element = new ArrayBufferWriter<byte>();
                    Encode(ref reader, element);
                    elements.Add(element.WrittenSpan.ToArray());
                }

                WriteComposite(output, ArrayTag, elements.Count, [.. elements.Select(static element => (Array.Empty<byte>(), element))]);
                break;
            default:
                // The last of a repeated key wins; then the keys in the order of their bytes.
                var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = reader.GetString()!;
                    reader.Read();
                    var value = new ArrayBufferWriter<byte>();
                    Encode(ref reader, value);
                    members[name] = value.WrittenSpan.ToArray();
                }

                var sorted = members.Select(static member =>
                {
                    var key = new ArrayBufferWriter<byte>();
                    WriteString(key, member.Key);
                    return (Key: key.WrittenSpan.ToArray(), Name: Encoding.UTF8.GetBytes(member.Key), member.Value);
                }).OrderBy(static member => member.Name, ByteOrder.Instance).Select(static member => (member.Key, member.Value)).ToArray();
                WriteComposite(output, ObjectTag, sorted.Length, sorted);
                break;
        }
    }

    private static void WriteComposite(ArrayBufferWriter<byte> output, byte tag, int count, (byte[] Key, byte[] Value)[] parts)
    {
        var isObject = tag == ObjectTag;
        var table = count * (isObject ? 8 : 4);
        var data = parts.Sum(static part => part.Key.Length + part.Value.Length);
        output.Write([tag]);
        WriteInt(output, 4 + table + data);
        WriteInt(output, count);

        // Keys first, then values: a lookup reads the keys close together.
        var offset = 0;
        var keyOffsets = new int[count];
        for (var i = 0; i < count; i++)
        {
            keyOffsets[i] = offset;
            offset += parts[i].Key.Length;
        }

        for (var i = 0; i < count; i++)
        {
            if (isObject)
            {
                WriteInt(output, keyOffsets[i]);
            }

            WriteInt(output, offset);
            offset += parts[i].Value.Length;
        }

        foreach (var part in parts)
        {
            output.Write(part.Key);
        }

        foreach (var part in parts)
        {
            output.Write(part.Value);
        }
    }

    private static int VerifyValue(ReadOnlySpan<byte> bytes, int position, int depth)
    {
        if (depth > JsonText.MaxDepth || position >= bytes.Length)
        {
            throw Damaged("a value is cut short");
        }

        switch (bytes[position++])
        {
            case NullTag or FalseTag or TrueTag:
                return position;
            case NumberTag:
                if (position >= bytes.Length || bytes[position] > 1)
                {
                    throw Damaged("a number has no sign");
                }

                var negative = bytes[position++] == 1;
                var count = Count(bytes, ref position);
                if (count == 0 || count > bytes.Length - position - 4)
                {
                    throw Damaged("a number is cut short");
                }

                var digits = bytes.Slice(position, (int)count);
                position += (int)count;
                var exponent = BinaryPrimitives.ReadInt32LittleEndian(bytes[position..]);
                position += 4;
                if (digits.IndexOfAnyExceptInRange((byte)'0', (byte)'9') >= 0
                    || (digits.Length > 1 && (digits[0] == '0' || digits[^1] == '0'))
                    || (digits is [(byte)'0'] && (negative || exponent != 0)))
                {
                    throw Damaged("a number is not written in its one form");
                }

                return position;
            case StringTag:
                return VerifyString(bytes, position);
            case ArrayTag or ObjectTag:
                var isObject = bytes[position - 1] == ObjectTag;
                var size = Int(bytes, position);
                var members = Int(bytes, position + 4);
                var end = position + 4 + size;
                var table = position + 8;
                var entry = isObject ? 8 : 4;
                if (size < 4 || end > bytes.Length || members > (size - 4) / entry)
                {
                    throw Damaged("an array or object is cut short");
                }

                var data = table + (members * entry);
                var next = data;
                ReadOnlySpan<byte> previous = default;
                if (isObject)
                {
                    for (var i = 0; i < members; i++)
                    {
                        if (data + Int(bytes, table + (i * entry)) != next)
                        {
                            throw Damaged("a key is not where the table says");
                        }

                        var start = next;
                        next = VerifyString(bytes[..end], next);
                        var at = start;
                        var length = (int)Count(bytes, ref at);
                        var name = bytes.Slice(at, length);
                        if (i > 0 && previous.SequenceCompareTo(name) >= 0)
                        {
                            throw Damaged("the keys of an object are not in order or repeat");
                        }

                        previous = name;
                    }
                }

                for (var i = 0; i < members; i++)
                {
                    if (data + Int(bytes, table + (i * entry) + (isObject ? 4 : 0)) != next)
                    {
                        throw Damaged("a value is not where the table says");
                    }

                    next = VerifyValue(bytes[..end], next, depth + 1);
                }

                return next == end ? end : throw Damaged("an array or object has bytes it does not use");
            default:
                throw Damaged("a value is of no known kind");
        }
    }

    private static int VerifyString(ReadOnlySpan<byte> bytes, int position)
    {
        var length = Count(bytes, ref position);
        if (length > bytes.Length - position || !System.Text.Unicode.Utf8.IsValid(bytes.Slice(position, (int)length)))
        {
            throw Damaged("a string is cut short or is not UTF-8");
        }

        return position + (int)length;
    }

    /// <summary>The length of a value that passed <see cref="Verify"/>.</summary>
    private static int Length(ReadOnlySpan<byte> bytes, int position)
    {
        var start = position;
        switch (bytes[position++])
        {
            case NullTag or FalseTag or TrueTag:
                return 1;
            case NumberTag:
                position++;
                var digits = (int)ReadCount(bytes, ref position);
                return position + digits + 4 - start;
            case StringTag:
                var length = (int)ReadCount(bytes, ref position);
                return position + length - start;
            default:
                return 1 + 4 + Int(bytes, position);
        }
    }

    private static void Write(ReadOnlySpan<byte> bytes, int position, StringBuilder text)
    {
        switch (bytes[position])
        {
            case NullTag:
                text.Append("null");
                break;
            case FalseTag:
                text.Append("false");
                break;
            case TrueTag:
                text.Append("true");
                break;
            case NumberTag:
                text.Append(Number(bytes, position).ToString());
                break;
            case StringTag:
                Quote(text, String(bytes, position));
                break;
            case ArrayTag:
                var count = Int(bytes, position + 5);
                var data = position + 9 + (count * 4);
                text.Append('[');
                for (var i = 0; i < count; i++)
                {
                    text.Append(i > 0 ? "," : string.Empty);
                    Write(bytes, data + Int(bytes, position + 9 + (i * 4)), text);
                }

                text.Append(']');
                break;
            default:
                var members = Int(bytes, position + 5);
                var start = position + 9 + (members * 8);
                text.Append('{');
                for (var i = 0; i < members; i++)
                {
                    text.Append(i > 0 ? "," : string.Empty);
                    var key = start + Int(bytes, position + 9 + (i * 8));
                    var length = (int)ReadCount(bytes, ref key);
                    Quote(text, TkText.Utf8.GetString(bytes.Slice(key, length)));
                    text.Append(':');
                    Write(bytes, start + Int(bytes, position + 9 + (i * 8) + 4), text);
                }

                text.Append('}');
                break;
        }
    }

    private static void Quote(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    text.Append("\\\"");
                    break;
                case '\\':
                    text.Append("\\\\");
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\r':
                    text.Append("\\r");
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                case '\b':
                    text.Append("\\b");
                    break;
                case '\f':
                    text.Append("\\f");
                    break;
                case < ' ':
                    text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    text.Append(c);
                    break;
            }
        }

        text.Append('"');
    }

    private static void WriteString(ArrayBufferWriter<byte> output, string value)
    {
        var bytes = TkText.Utf8.GetBytes(value);
        WriteCount(output, (uint)bytes.Length);
        output.Write(bytes);
    }

    private static void WriteInt(ArrayBufferWriter<byte> output, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(output.GetSpan(4), value);
        output.Advance(4);
    }

    private static void WriteCount(ArrayBufferWriter<byte> output, uint value)
    {
        while (value >= 0x80)
        {
            output.Write([(byte)(value | 0x80)]);
            value >>= 7;
        }

        output.Write([(byte)value]);
    }

    /// <summary>A count in a document that passed <see cref="Verify"/>.</summary>
    private static uint ReadCount(ReadOnlySpan<byte> bytes, ref int position)
    {
        uint value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = bytes[position++];
            value |= (uint)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value;
            }
        }
    }

    /// <summary>A count, checked: in its shortest form, and within the bytes.</summary>
    private static uint Count(ReadOnlySpan<byte> bytes, ref int position)
    {
        uint value = 0;
        for (var shift = 0; shift <= 28; shift += 7)
        {
            if (position >= bytes.Length)
            {
                throw Damaged("a count is cut short");
            }

            var b = bytes[position++];
            if (shift == 28 && b > 0x0F)
            {
                break;
            }

            value |= (uint)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return b == 0 && shift > 0 ? throw Damaged("a count is not in its shortest form") : value;
            }
        }

        throw Damaged("a count is too long");
    }

    private static int Int(ReadOnlySpan<byte> bytes, int position) =>
        position >= 0 && position <= bytes.Length - 4 && BinaryPrimitives.ReadInt32LittleEndian(bytes[position..]) is var value and >= 0
            ? value
            : throw Damaged("a length or position is outside the document");

    private static InvalidDataException Damaged(string why) => new($"A JSON document is damaged: {why}.");

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
