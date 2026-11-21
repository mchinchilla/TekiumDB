// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TekiumDB;

/// <summary>How a JSON document is kept.</summary>
public enum TkJsonFormat
{
    /// <summary>The text as it was written, checked to be JSON: spaces, key order and repeated keys stay.</summary>
    Text = 0,

    /// <summary>A binary form: keys sorted and not repeated (the last one wins), each number written once.</summary>
    Binary = 1,
}

/// <summary>
/// A JSON document, immutable: the value of a <c>json</c> or <c>jsonb</c> column. It is checked when
/// created, so it is always a whole, valid document; <c>default</c> is the document <c>null</c>, which is
/// not the same as a column with no value. Two documents are equal when they say the same, whatever
/// their form.
/// </summary>
public readonly struct TkJson : IEquatable<TkJson>
{
    private static readonly byte[] NullText = "null"u8.ToArray();

    private readonly byte[]? _bytes;

    private TkJson(byte[] bytes, TkJsonFormat format)
    {
        _bytes = bytes;
        Format = format;
    }

    /// <summary>Gets the document <c>null</c>.</summary>
    public static TkJson Null => default;

    /// <summary>Gets how the document is kept.</summary>
    public TkJsonFormat Format { get; }

    /// <summary>Gets what kind of value the document is.</summary>
    public JsonValueKind ValueKind => Format == TkJsonFormat.Binary ? JsonBinary.Kind(Bytes, 1) : JsonText.Kind(Bytes);

    /// <summary>Gets the bytes as they are kept: UTF-8 text, or the binary form.</summary>
    internal ReadOnlySpan<byte> Bytes => _bytes ?? NullText;

    /// <summary>Compares two documents by what they say.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they say the same.</returns>
    public static bool operator ==(TkJson left, TkJson right) => left.Equals(right);

    /// <summary>Compares two documents by what they say.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(TkJson left, TkJson right) => !left.Equals(right);

    /// <summary>Reads a document from its text. Throws <see cref="FormatException"/> if it is not strict JSON.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The document, kept as text.</returns>
    public static TkJson Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes;
        try
        {
            bytes = TkText.Utf8.GetBytes(text);
        }
        catch (EncoderFallbackException invalid)
        {
            throw new FormatException("The text is not valid JSON: it is not valid Unicode.", invalid);
        }

        return Parse(bytes);
    }

    /// <summary>Reads a document from its UTF-8 text. Throws <see cref="FormatException"/> if it is not strict JSON.</summary>
    /// <param name="utf8">The text.</param>
    /// <returns>The document, kept as text.</returns>
    public static TkJson Parse(ReadOnlySpan<byte> utf8) =>
        JsonText.Check(utf8) is { } error ? throw new FormatException($"The text is not valid JSON: {error}.") : new TkJson(utf8.ToArray(), TkJsonFormat.Text);

    /// <summary>Reads a document from its text, without throwing.</summary>
    /// <param name="text">The text.</param>
    /// <param name="json">The document, if it is one.</param>
    /// <returns>Whether it is one.</returns>
    public static bool TryParse(string text, out TkJson json)
    {
        try
        {
            json = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            json = default;
            return false;
        }
    }

    /// <summary>A document from its binary form, checked whole. Throws <see cref="InvalidDataException"/> if the bytes are not one.</summary>
    /// <param name="binary">The binary form, as <see cref="GetBinary"/> gives it.</param>
    /// <returns>The document.</returns>
    public static TkJson FromBinary(ReadOnlySpan<byte> binary)
    {
        JsonBinary.Verify(binary);
        return new TkJson(binary.ToArray(), TkJsonFormat.Binary);
    }

    /// <summary>The document of a value of the application, written by System.Text.Json with its type information.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">How to write it: from a <c>JsonSerializerContext</c>, which needs no reflection.</param>
    /// <returns>The document, kept as text.</returns>
    public static TkJson Serialize<T>(T value, JsonTypeInfo<T> typeInfo) =>
        new(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), TkJsonFormat.Text);

    /// <summary>Reads the document as a value of the application.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="typeInfo">How to read it: from a <c>JsonSerializerContext</c>.</param>
    /// <returns>The value.</returns>
    public T? Deserialize<T>(JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(Utf8Text(), typeInfo);

    /// <summary>The document as a <see cref="JsonElement"/> that does not need disposing.</summary>
    /// <returns>The element.</returns>
    public JsonElement ToElement()
    {
        using var document = JsonDocument.Parse(Utf8Text());
        return document.RootElement.Clone();
    }

    /// <summary>The same document in its binary form.</summary>
    /// <returns>The document, kept as binary.</returns>
    public TkJson ToBinary() => Format == TkJsonFormat.Binary ? this : new TkJson(JsonBinary.FromText(Bytes), TkJsonFormat.Binary);

    /// <summary>The same document as text: itself if it is text, its canonical text if it is binary.</summary>
    /// <returns>The document, kept as text.</returns>
    public TkJson ToText() => Format == TkJsonFormat.Text ? this : new TkJson(Utf8Text(), TkJsonFormat.Text);

    /// <summary>Gets the binary form, as <see cref="FromBinary"/> reads it.</summary>
    /// <returns>The bytes.</returns>
    public ReadOnlySpan<byte> GetBinary() => ToBinary().Bytes;

    /// <summary>The value at a path, if the document has one there.</summary>
    /// <param name="path">The path.</param>
    /// <param name="value">The value, in the form of this document. Inside a text document it is the text it had.</param>
    /// <returns>Whether there is a value at the path.</returns>
    public bool TryGet(TkJsonPath path, out TkJson value)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Format == TkJsonFormat.Binary)
        {
            if (JsonBinary.Find(Bytes, path) is var (start, length) && length > 0)
            {
                var bytes = new byte[length + 1];
                bytes[0] = JsonBinary.Version;
                Bytes.Slice(start, length).CopyTo(bytes.AsSpan(1));
                value = new TkJson(bytes, TkJsonFormat.Binary);
                return true;
            }
        }
        else if (JsonText.Find(Bytes, path) is var (start, length) && length > 0)
        {
            value = new TkJson(Bytes.Slice(start, length).ToArray(), TkJsonFormat.Text);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// The text at a path: a string, or the text of an array or object; null if there is nothing there, or
    /// something else. It is what an index on the path holds, so a LINQ condition on it uses the index.
    /// </summary>
    /// <param name="path">The path, starting with <c>$</c>.</param>
    /// <returns>The text, or null.</returns>
    public string? TextAt(string path) => TkJsonIndexing.Value(this, TkJsonPath.Parse(path), TkColumnType.Text) as string;

    /// <summary>
    /// The number at a path, as the nearest <see cref="double"/>; null if there is nothing there, or
    /// something else. It is what an index on the path holds, so a LINQ condition on it uses the index.
    /// </summary>
    /// <param name="path">The path, starting with <c>$</c>.</param>
    /// <returns>The number, or null.</returns>
    public double? NumberAt(string path) => TkJsonIndexing.Value(this, TkJsonPath.Parse(path), TkColumnType.F64) as double?;

    /// <summary>The text of a string document. Throws <see cref="InvalidOperationException"/> if it is not one.</summary>
    /// <returns>The text.</returns>
    public string GetString() => ValueKind == JsonValueKind.String
        ? (Format == TkJsonFormat.Binary ? JsonBinary.String(Bytes, 1) : JsonText.String(Bytes))
        : throw NotA("string");

    /// <summary>The value of <c>true</c> or <c>false</c>. Throws <see cref="InvalidOperationException"/> otherwise.</summary>
    /// <returns>The value.</returns>
    public bool GetBoolean() => ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw NotA("boolean"),
    };

    /// <summary>A number as an exact decimal. Throws <see cref="OverflowException"/> if it does not fit.</summary>
    /// <returns>The value.</returns>
    public decimal GetDecimal() => Number().ToDecimal();

    /// <summary>A number as a whole number. Throws <see cref="OverflowException"/> if it is not one or does not fit.</summary>
    /// <returns>The value.</returns>
    public long GetInt64() => Number().ToInt64();

    /// <summary>A number as a floating number, the nearest one; infinity if it is too big.</summary>
    /// <returns>The value.</returns>
    public double GetDouble() => Number().ToDouble();

    /// <summary>A number as exactly as it can be held: a whole number, else a decimal, else a floating number.</summary>
    /// <returns>A <see cref="long"/>, a <see cref="decimal"/> or a <see cref="double"/>.</returns>
    public object GetNumber() => Number().Best();

    /// <inheritdoc />
    public bool Equals(TkJson other) =>
        Format == TkJsonFormat.Binary && other.Format == TkJsonFormat.Binary
            ? Bytes.SequenceEqual(other.Bytes)
            : GetBinary().SequenceEqual(other.GetBinary());

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TkJson other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(GetBinary());
        return hash.ToHashCode();
    }

    /// <summary>The text of the document: as it was written if it is text, canonical if it is binary.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => TkText.Utf8.GetString(Utf8Text());

    /// <summary>The same text document without spaces between its tokens: order, repeated keys and the text of numbers and strings stay.</summary>
    internal TkJson Compact() => Format == TkJsonFormat.Binary ? this : new TkJson(JsonText.Compact(Bytes), TkJsonFormat.Text);

    /// <summary>Whether a number is written as a whole number: in a text document, by its text, as SQLite says; in a binary one, by its value.</summary>
    internal bool IsWholeNumber() => Format == TkJsonFormat.Binary
        ? Number().Exponent >= 0
        : JsonText.Trimmed(Bytes).IndexOfAny("eE."u8) < 0;

    /// <summary>A document kept as it is read from a row, checked whole.</summary>
    internal static TkJson FromStored(ReadOnlySpan<byte> bytes, TkJsonFormat format)
    {
        if (format == TkJsonFormat.Binary)
        {
            return FromBinary(bytes);
        }

        return JsonText.Check(bytes) is { } error ? throw new InvalidDataException($"A stored JSON document is damaged: {error}.") : new TkJson(bytes.ToArray(), TkJsonFormat.Text);
    }

    /// <summary>The document in the form a column keeps.</summary>
    internal TkJson As(TkJsonFormat format) => format == TkJsonFormat.Binary ? ToBinary() : ToText();

    internal JsonNumber Number() => ValueKind == JsonValueKind.Number
        ? (Format == TkJsonFormat.Binary ? JsonBinary.Number(Bytes, 1) : JsonNumber.Parse(JsonText.Trimmed(Bytes)))
        : throw NotA("number");

    private byte[] Utf8Text() => Format == TkJsonFormat.Binary ? JsonBinary.ToText(Bytes) : Bytes.ToArray();

    private static InvalidOperationException NotA(string what) => new($"The JSON value is not a {what}.");
}

/// <summary>
/// A JSON number, exactly: its digits, without leading or trailing zeros, and the power of ten they are
/// multiplied by. Zero is the digit 0 with power 0, and has no sign.
/// </summary>
internal readonly record struct JsonNumber(bool Negative, string Digits, int Exponent)
{
    public static JsonNumber Parse(ReadOnlySpan<byte> text)
    {
        var position = 0;
        var negative = text[0] == (byte)'-';
        position += negative ? 1 : 0;
        var digits = new StringBuilder();
        var exponent = 0L;
        while (position < text.Length && char.IsAsciiDigit((char)text[position]))
        {
            digits.Append((char)text[position++]);
        }

        if (position < text.Length && text[position] == (byte)'.')
        {
            position++;
            while (position < text.Length && char.IsAsciiDigit((char)text[position]))
            {
                digits.Append((char)text[position++]);
                exponent--;
            }
        }

        if (position < text.Length && text[position] is (byte)'e' or (byte)'E')
        {
            position++;
            var sign = 1;
            if (text[position] is (byte)'+' or (byte)'-')
            {
                sign = text[position++] == (byte)'-' ? -1 : 1;
            }

            var power = 0L;
            while (position < text.Length)
            {
                power = Math.Min((power * 10) + (text[position++] - '0'), 1_000_000_000L);
            }

            exponent += sign * power;
        }

        return Normalize(negative, digits.ToString(), exponent);
    }

    public static JsonNumber Normalize(bool negative, string digits, long exponent)
    {
        var start = 0;
        while (start < digits.Length - 1 && digits[start] == '0')
        {
            start++;
        }

        var end = digits.Length;
        while (end > start + 1 && digits[end - 1] == '0')
        {
            end--;
            exponent++;
        }

        var kept = digits[start..end];
        return kept == "0"
            ? new JsonNumber(false, "0", 0)
            : new JsonNumber(negative, kept, (int)Math.Clamp(exponent, int.MinValue / 2, int.MaxValue / 2));
    }

    /// <summary>The shortest text that says the number: as JavaScript writes numbers, but exactly.</summary>
    public override string ToString()
    {
        var text = new StringBuilder(Negative ? "-" : string.Empty);
        var length = Digits.Length;
        var point = (long)length + Exponent;
        if (Exponent >= 0 && point <= 21)
        {
            text.Append(Digits).Append('0', Exponent);
        }
        else if (point > 0 && point <= 21)
        {
            text.Append(Digits, 0, (int)point).Append('.').Append(Digits, (int)point, length - (int)point);
        }
        else if (point > -6 && point <= 0)
        {
            text.Append("0.").Append('0', (int)-point).Append(Digits);
        }
        else
        {
            text.Append(Digits[0]);
            if (length > 1)
            {
                text.Append('.').Append(Digits, 1, length - 1);
            }

            var power = point - 1;
            text.Append('e').Append(power >= 0 ? "+" : "-").Append(Math.Abs(power).ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    public decimal ToDecimal()
    {
        if (Exponent > 28 || Digits.Length + Exponent > 29 || Exponent < -28 - Digits.Length)
        {
            throw new OverflowException($"The JSON number {this} does not fit a decimal.");
        }

        return decimal.Parse(ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public long ToInt64() => Exponent >= 0 && Digits.Length + Exponent <= 19 && long.TryParse(ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
        ? whole
        : throw new OverflowException($"The JSON number {this} is not a whole number that fits a long.");

    public double ToDouble() => double.Parse(ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    public object Best()
    {
        if (Exponent >= 0 && Digits.Length + Exponent <= 19 && long.TryParse(ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
        {
            return whole;
        }

        try
        {
            var exact = ToDecimal();
            return Normalize(exact < 0, Math.Abs(exact).ToString(CultureInfo.InvariantCulture).Replace(".", string.Empty, StringComparison.Ordinal), -Scale(exact)) == this ? exact : ToDouble();
        }
        catch (OverflowException)
        {
            return ToDouble();
        }

        static int Scale(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;
    }
}
