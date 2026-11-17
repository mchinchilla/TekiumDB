// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections;
using System.Data.Common;
using System.Globalization;
using Tekium.Embedded.Sql.Execution;

namespace Tekium.Embedded.Sql;

/// <summary>
/// The rows a statement returned. They are all in memory by the time the reader exists, so reading them,
/// synchronously or not, does no I/O.
/// </summary>
public sealed class TkDataReader : DbDataReader, IEnumerable<System.Data.IDataRecord>
{
    private readonly SqlResult _result;
    private int _row = -1;
    private bool _closed;

    internal TkDataReader(SqlResult result) => _result = result;

    /// <inheritdoc/>
    public override int Depth => 0;

    /// <inheritdoc/>
    public override int FieldCount => _result.Names.Length;

    /// <inheritdoc/>
    public override bool HasRows => _result.Rows.Count > 0;

    /// <inheritdoc/>
    public override bool IsClosed => _closed;

    /// <inheritdoc/>
    public override int RecordsAffected => _result.RecordsAffected;

    /// <inheritdoc/>
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc/>
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <inheritdoc/>
    public override void Close() => _closed = true;

    /// <inheritdoc/>
    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(Value(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override byte GetByte(int ordinal) => Convert.ToByte(Value(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = (byte[])Value(ordinal);
        if (buffer is null)
        {
            return bytes.Length;
        }

        var count = (int)Math.Max(0, Math.Min(length, bytes.Length - dataOffset));
        Array.Copy(bytes, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    /// <inheritdoc/>
    public override char GetChar(int ordinal) => Convert.ToChar(Value(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = GetString(ordinal);
        if (buffer is null)
        {
            return text.Length;
        }

        var count = (int)Math.Max(0, Math.Min(length, text.Length - dataOffset));
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    /// <inheritdoc/>
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    /// <inheritdoc/>
    public override DateTime GetDateTime(int ordinal) => Value(ordinal) switch
    {
        DateTime moment => moment,
        DateTimeOffset moment => moment.UtcDateTime,
        DateOnly day => day.ToDateTime(TimeOnly.MinValue),
        var other => Convert.ToDateTime(other, CultureInfo.InvariantCulture),
    };

    /// <inheritdoc/>
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(Value(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override double GetDouble(int ordinal) => Convert.ToDouble(Value(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override IEnumerator GetEnumerator() => ((IEnumerable<System.Data.IDataRecord>)this).GetEnumerator();

    /// <inheritdoc/>
    IEnumerator<System.Data.IDataRecord> IEnumerable<System.Data.IDataRecord>.GetEnumerator()
    {
        while (Read())
        {
            yield return this;
        }
    }

    /// <inheritdoc/>
    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal)
    {
        // One of the types a column can hold, named here so that a trimmed application keeps its members.
        var type = _result.Types[ordinal];
        return type == typeof(bool) ? typeof(bool)
            : type == typeof(byte) ? typeof(byte)
            : type == typeof(sbyte) ? typeof(sbyte)
            : type == typeof(short) ? typeof(short)
            : type == typeof(ushort) ? typeof(ushort)
            : type == typeof(int) ? typeof(int)
            : type == typeof(uint) ? typeof(uint)
            : type == typeof(long) ? typeof(long)
            : type == typeof(ulong) ? typeof(ulong)
            : type == typeof(float) ? typeof(float)
            : type == typeof(double) ? typeof(double)
            : type == typeof(decimal) ? typeof(decimal)
            : type == typeof(char) ? typeof(char)
            : type == typeof(Guid) ? typeof(Guid)
            : type == typeof(DateTime) ? typeof(DateTime)
            : type == typeof(DateTimeOffset) ? typeof(DateTimeOffset)
            : type == typeof(TimeSpan) ? typeof(TimeSpan)
            : type == typeof(DateOnly) ? typeof(DateOnly)
            : type == typeof(TimeOnly) ? typeof(TimeOnly)
            : type == typeof(string) ? typeof(string)
            : type == typeof(byte[]) ? typeof(byte[])

            // A JSON document reads as its text, which is what a string property or Dapper expects; GetFieldValue<TkJson> gives the document.
            : type == typeof(TkJson) ? typeof(string)
            : typeof(object);
    }

    /// <inheritdoc/>
    public override float GetFloat(int ordinal) => Convert.ToSingle(Value(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override Guid GetGuid(int ordinal) => Value(ordinal) is Guid id ? id : Guid.Parse(GetString(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override short GetInt16(int ordinal) => Convert.ToInt16(Value(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override int GetInt32(int ordinal) => Convert.ToInt32(Value(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override long GetInt64(int ordinal) => Convert.ToInt64(Value(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override string GetName(int ordinal) => _result.Names[ordinal];

    /// <inheritdoc/>
    public override int GetOrdinal(string name)
    {
        var ordinal = Array.FindIndex(_result.Names, candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
        return ordinal >= 0 ? ordinal : throw new ArgumentException($"The result has no column '{name}'.", nameof(name));
    }

    /// <inheritdoc/>
    public override string GetString(int ordinal) => Value(ordinal) switch
    {
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString() ?? string.Empty,
    };

    /// <inheritdoc/>
    public override object GetValue(int ordinal) => Row()[ordinal] switch
    {
        null => DBNull.Value,
        TkJson json => json.ToString(),
        var value => value,
    };

    /// <inheritdoc/>
    public override T GetFieldValue<T>(int ordinal) =>
        typeof(T) == typeof(TkJson) && Row()[ordinal] is TkJson json ? (T)(object)json
        : typeof(T) == typeof(TkJson) && Row()[ordinal] is string text ? (T)(object)TkJson.Parse(text)
        : base.GetFieldValue<T>(ordinal);

    /// <inheritdoc/>
    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    /// <inheritdoc/>
    public override bool IsDBNull(int ordinal) => Row()[ordinal] is null;

    /// <inheritdoc/>
    public override bool NextResult() => false;

    /// <inheritdoc/>
    public override bool Read() => !_closed && ++_row < _result.Rows.Count;

    private object?[] Row() =>
        _row >= 0 && _row < _result.Rows.Count ? _result.Rows[_row] : throw new InvalidOperationException("There is no current row: call Read first, and stop when it returns false.");

    private object Value(int ordinal) => Row()[ordinal] ?? throw new InvalidCastException($"Column '{GetName(ordinal)}' is NULL.");
}
