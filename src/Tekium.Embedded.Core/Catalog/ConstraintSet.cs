// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Tekium.Embedded.Catalog;

/// <summary>A FOREIGN KEY as the catalog knows it.</summary>
/// <param name="Name">The name of the constraint.</param>
/// <param name="Table">The table referred to.</param>
/// <param name="Index">The name of the index of the referring table that starts with the column.</param>
/// <param name="IsNullable">Whether the column may have no value, which shows in how the index holds it.</param>
internal readonly record struct ForeignKeyInfo(string Name, string Table, string Index, bool IsNullable);

/// <summary>A FOREIGN KEY seen from the table referred to: who must be asked before deleting a row.</summary>
/// <param name="Table">The referring table.</param>
/// <param name="Name">The name of the constraint.</param>
/// <param name="Index">The name of the index of the referring table that starts with the column.</param>
/// <param name="IsNullable">Whether the column may have no value.</param>
internal readonly record struct Referrer(string Table, string Name, string Index, bool IsNullable);

/// <summary>
/// The CHECK and FOREIGN KEY constraints of a table, as a row type declares them and as the catalog
/// stores them. (A UNIQUE constraint is a property of its index, and lives with it.)
/// </summary>
/// <remarks>
/// <see cref="Encoded"/> is file format, and empty when there are none. Layout: a format byte; a
/// two-byte count of checks and, for each, its name and its text; a two-byte count of foreign keys and,
/// for each, its name, the table referred to, the name of its index and whether its column is nullable.
/// Names carry a one-byte length; texts, a four-byte one.
/// </remarks>
internal sealed class ConstraintSet
{
    /// <summary>The longest constraint name, in UTF-8 bytes.</summary>
    public const int MaxNameBytes = 128;

    private const byte Format = 1;

    private ConstraintSet(TkCheck[] checks, ForeignKeyInfo[] foreignKeys, byte[] encoded)
    {
        Checks = checks;
        ForeignKeys = foreignKeys;
        Encoded = encoded;
    }

    public TkCheck[] Checks { get; }

    public ForeignKeyInfo[] ForeignKeys { get; }

    public byte[] Encoded { get; }

    /// <summary>The constraints a row type declares, checked.</summary>
    public static ConstraintSet Build<T>(string table, IndexDefinition[] indexes)
        where T : class, ITkRow<T>
    {
        var checks = T.Checks.ToArray();
        var declared = T.ForeignKeys;
        var columns = T.Columns;
        var foreignKeys = new ForeignKeyInfo[declared.Length];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in checks)
        {
            Validate(table, check.Name, names);
        }

        for (var i = 0; i < declared.Length; i++)
        {
            var key = declared[i];
            Validate(table, key.Name, names);
            if (key.Column >= columns.Length
                || key.Index >= indexes.Length
                || indexes[key.Index].Fields[0] != key.Column
                || indexes[key.Index].Where.Length > 0
                || indexes[key.Index].IsZoneMap
                || Encoding.UTF8.GetByteCount(key.Table) > CatalogStore.MaxNameBytes)
            {
                throw new TkSchemaException(
                    $"Table '{table}': FOREIGN KEY '{key.Name}' must be on a column that a full index of the table starts with.");
            }

            foreignKeys[i] = new ForeignKeyInfo(key.Name, key.Table, indexes[key.Index].Name, columns[key.Column].IsNullable);
        }

        return new ConstraintSet(checks, foreignKeys, Encode(checks, foreignKeys));
    }

    public static ConstraintSet Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty)
        {
            return new ConstraintSet([], [], []);
        }

        var stored = encoded.ToArray();
        var position = 1;
        if (encoded[0] != Format)
        {
            throw Corrupt();
        }

        var checks = new TkCheck[ReadCount(encoded, ref position)];
        for (var i = 0; i < checks.Length; i++)
        {
            checks[i] = new TkCheck(ReadName(encoded, ref position), ReadText(encoded, ref position));
        }

        var foreignKeys = new ForeignKeyInfo[ReadCount(encoded, ref position)];
        for (var i = 0; i < foreignKeys.Length; i++)
        {
            foreignKeys[i] = new ForeignKeyInfo(
                ReadName(encoded, ref position), ReadName(encoded, ref position), ReadName(encoded, ref position), ReadFlag(encoded, ref position));
        }

        return position == encoded.Length ? new ConstraintSet(checks, foreignKeys, stored) : throw Corrupt();
    }

    /// <summary>The list of those who refer to a table, as its catalog entry stores it: empty when nobody does.</summary>
    public static byte[] EncodeReferrers(IReadOnlyList<Referrer> referrers)
    {
        if (referrers.Count == 0)
        {
            return [];
        }

        var encoded = new ArrayBufferWriter<byte>();
        Write(encoded, Format);
        WriteCount(encoded, referrers.Count);
        foreach (var referrer in referrers)
        {
            WriteName(encoded, referrer.Table);
            WriteName(encoded, referrer.Name);
            WriteName(encoded, referrer.Index);
            Write(encoded, referrer.IsNullable ? (byte)1 : (byte)0);
        }

        return encoded.WrittenSpan.ToArray();
    }

    public static List<Referrer> DecodeReferrers(ReadOnlySpan<byte> encoded)
    {
        var referrers = new List<Referrer>();
        if (encoded.IsEmpty)
        {
            return referrers;
        }

        var position = 1;
        if (encoded[0] != Format)
        {
            throw Corrupt();
        }

        for (var count = ReadCount(encoded, ref position); count > 0; count--)
        {
            referrers.Add(new Referrer(
                ReadName(encoded, ref position), ReadName(encoded, ref position), ReadName(encoded, ref position), ReadFlag(encoded, ref position)));
        }

        return position == encoded.Length ? referrers : throw Corrupt();
    }

    private static void Validate(string table, string name, HashSet<string> names)
    {
        if (Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
        {
            throw new TkSchemaException($"Table '{table}': the name of constraint '{name}' exceeds {MaxNameBytes} UTF-8 bytes.");
        }

        if (!names.Add(name))
        {
            throw new TkSchemaException($"Table '{table}' declares constraint '{name}' twice.");
        }
    }

    private static byte[] Encode(TkCheck[] checks, ForeignKeyInfo[] foreignKeys)
    {
        if (checks.Length == 0 && foreignKeys.Length == 0)
        {
            return [];
        }

        var encoded = new ArrayBufferWriter<byte>();
        Write(encoded, Format);
        WriteCount(encoded, checks.Length);
        foreach (var check in checks)
        {
            WriteName(encoded, check.Name);
            BinaryPrimitives.WriteInt32LittleEndian(encoded.GetSpan(sizeof(int)), Encoding.UTF8.GetByteCount(check.Text));
            encoded.Advance(sizeof(int));
            Encoding.UTF8.GetBytes(check.Text, encoded);
        }

        WriteCount(encoded, foreignKeys.Length);
        foreach (var key in foreignKeys)
        {
            WriteName(encoded, key.Name);
            WriteName(encoded, key.Table);
            WriteName(encoded, key.Index);
            Write(encoded, key.IsNullable ? (byte)1 : (byte)0);
        }

        return encoded.WrittenSpan.ToArray();
    }

    private static void Write(ArrayBufferWriter<byte> encoded, byte value)
    {
        encoded.GetSpan(1)[0] = value;
        encoded.Advance(1);
    }

    private static void WriteCount(ArrayBufferWriter<byte> encoded, int count)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.GetSpan(sizeof(ushort)), checked((ushort)count));
        encoded.Advance(sizeof(ushort));
    }

    private static void WriteName(ArrayBufferWriter<byte> encoded, string name)
    {
        Write(encoded, checked((byte)Encoding.UTF8.GetByteCount(name)));
        Encoding.UTF8.GetBytes(name, encoded);
    }

    private static int ReadCount(ReadOnlySpan<byte> encoded, ref int position)
    {
        if (encoded.Length - position < sizeof(ushort))
        {
            throw Corrupt();
        }

        position += sizeof(ushort);
        return BinaryPrimitives.ReadUInt16LittleEndian(encoded[(position - sizeof(ushort))..]);
    }

    private static bool ReadFlag(ReadOnlySpan<byte> encoded, ref int position) =>
        position < encoded.Length && encoded[position] <= 1 ? encoded[position++] == 1 : throw Corrupt();

    private static string ReadName(ReadOnlySpan<byte> encoded, ref int position)
    {
        if (position >= encoded.Length || encoded[position] == 0 || encoded[position] > encoded.Length - position - 1)
        {
            throw Corrupt();
        }

        var length = encoded[position];
        position += 1 + length;
        return Encoding.UTF8.GetString(encoded.Slice(position - length, length));
    }

    private static string ReadText(ReadOnlySpan<byte> encoded, ref int position)
    {
        if (encoded.Length - position < sizeof(int)
            || BinaryPrimitives.ReadInt32LittleEndian(encoded[position..]) is var length && (length < 0 || length > encoded.Length - position - sizeof(int)))
        {
            throw Corrupt();
        }

        position += sizeof(int) + length;
        return Encoding.UTF8.GetString(encoded.Slice(position - length, length));
    }

    private static InvalidDataException Corrupt() => new("The constraints of a table in the catalog are damaged.");
}
