// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Text.Json;

namespace Tekium.Embedded;

/// <summary>
/// JSON kept as its text: checked with the strict reader of System.Text.Json (no comments, no trailing
/// commas, one value), and read by path by walking the text. A repeated key gives its first value.
/// </summary>
internal static class JsonText
{
    public const int MaxDepth = 256;

    private static readonly JsonReaderOptions Strict = new() { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = MaxDepth };

    /// <summary>Why the text is not a JSON document, or null if it is one.</summary>
    public static string? Check(ReadOnlySpan<byte> utf8)
    {
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return "it starts with a byte order mark";
        }

        var reader = new Utf8JsonReader(utf8, Strict);
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
                {
                    // An escape that is half of a surrogate pair is not text.
                    _ = reader.GetString();
                }
            }

            return reader.BytesConsumed == 0 ? "it is empty" : null;
        }
        catch (JsonException invalid)
        {
            return invalid.Message.TrimEnd('.');
        }
        catch (InvalidOperationException invalid)
        {
            return invalid.Message.TrimEnd('.');
        }
    }

    public static JsonValueKind Kind(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, Strict);
        reader.Read();
        return reader.TokenType switch
        {
            JsonTokenType.StartObject => JsonValueKind.Object,
            JsonTokenType.StartArray => JsonValueKind.Array,
            JsonTokenType.String => JsonValueKind.String,
            JsonTokenType.Number => JsonValueKind.Number,
            JsonTokenType.True => JsonValueKind.True,
            JsonTokenType.False => JsonValueKind.False,
            _ => JsonValueKind.Null,
        };
    }

    public static string String(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, Strict);
        reader.Read();
        return reader.GetString()!;
    }

    /// <summary>The bytes of a scalar without the spaces around it.</summary>
    public static ReadOnlySpan<byte> Trimmed(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, Strict);
        reader.Read();
        return utf8.Slice((int)reader.TokenStartIndex, (int)(reader.BytesConsumed - reader.TokenStartIndex));
    }

    /// <summary>The text without spaces between tokens; everything else, as it was written.</summary>
    public static byte[] Compact(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, Strict);
        var output = new ArrayBufferWriter<byte>(utf8.Length);
        var previous = JsonTokenType.None;
        while (reader.Read())
        {
            var token = reader.TokenType;
            if (previous is not (JsonTokenType.None or JsonTokenType.StartObject or JsonTokenType.StartArray or JsonTokenType.PropertyName)
                && token is not (JsonTokenType.EndObject or JsonTokenType.EndArray))
            {
                output.Write(","u8);
            }

            switch (token)
            {
                case JsonTokenType.StartObject:
                    output.Write("{"u8);
                    break;
                case JsonTokenType.EndObject:
                    output.Write("}"u8);
                    break;
                case JsonTokenType.StartArray:
                    output.Write("["u8);
                    break;
                case JsonTokenType.EndArray:
                    output.Write("]"u8);
                    break;
                case JsonTokenType.PropertyName:
                    output.Write("\""u8);
                    output.Write(reader.ValueSpan);
                    output.Write("\":"u8);
                    break;
                case JsonTokenType.String:
                    output.Write("\""u8);
                    output.Write(reader.ValueSpan);
                    output.Write("\""u8);
                    break;
                default:
                    output.Write(reader.ValueSpan);
                    break;
            }

            previous = token;
        }

        return output.WrittenSpan.ToArray();
    }

    /// <summary>Where the value at a path is in the text: its start and length. Length 0 if there is none.</summary>
    public static (int Start, int Length) Find(ReadOnlySpan<byte> utf8, TkJsonPath path)
    {
        var reader = new Utf8JsonReader(utf8, Strict);
        reader.Read();
        foreach (var step in path.Steps)
        {
            if (step.Name is { } name)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    return default;
                }

                var found = false;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var matches = reader.ValueTextEquals(name);
                    reader.Read();
                    if (matches)
                    {
                        found = true;
                        break;
                    }

                    reader.Skip();
                }

                if (!found)
                {
                    return default;
                }
            }
            else
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    return default;
                }

                var index = step.Index;
                if (step.IsFromEnd)
                {
                    // How many there are, on a copy of the reader that walks them without moving this one.
                    var counter = reader;
                    var count = 0;
                    while (counter.Read() && counter.TokenType != JsonTokenType.EndArray)
                    {
                        counter.Skip();
                        count++;
                    }

                    index = count - step.Index;
                    if (index < 0)
                    {
                        return default;
                    }
                }

                var at = -1;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (++at == index)
                    {
                        break;
                    }

                    reader.Skip();
                }

                if (at != index || reader.TokenType == JsonTokenType.EndArray)
                {
                    return default;
                }
            }
        }

        var start = (int)reader.TokenStartIndex;
        reader.Skip();
        return (start, (int)reader.BytesConsumed - start);
    }
}
