// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Text;

namespace TekiumDB.Sql.Procedures;

/// <summary>What the machine of stored procedures can do. The numbers are file format.</summary>
internal enum PlOp : byte
{
    /// <summary>register ← expression. Operands: register, constant.</summary>
    Evaluate = 1,

    /// <summary>Continue elsewhere. Operand: where.</summary>
    Jump = 2,

    /// <summary>Continue elsewhere unless the register holds TRUE. Operands: register, where.</summary>
    JumpUnless = 3,

    /// <summary>Run an INSERT, UPDATE or DELETE. Operand: constant.</summary>
    Execute = 4,

    /// <summary>Run a query and keep its rows in a register. Operands: register, constant.</summary>
    Open = 5,

    /// <summary>Put the next row in registers, or continue elsewhere if there are no more. Operands: rows, first register, how many, where.</summary>
    Next = 6,

    /// <summary>Run a query and put its first row in registers (NULL if none). Operands: constant, how many, each register.</summary>
    Into = 7,

    /// <summary>Call a procedure. Operands: its name (constant), register for the result or none, how many arguments, each register.</summary>
    Call = 8,

    /// <summary>End, with the value of a register or with nothing. Operand: register or none.</summary>
    Return = 9,

    /// <summary>End with an error whose message is in a register. Operand: register.</summary>
    Raise = 10,
}

/// <summary>What a constant of a program is.</summary>
internal enum PlConstant : byte
{
    Expression = 0,
    Statement = 1,
    Name = 2,
}

/// <summary>
/// A compiled stored procedure, as the catalog keeps it: its signature, its registers, its constants
/// (the expressions and SQL statements, as text, and the names of the procedures it calls) and its
/// code, a flat run of instructions over registers. The layout is file format.
/// </summary>
internal sealed class PlProgram
{
    /// <summary>The operand that means "no register".</summary>
    public const ushort None = ushort.MaxValue;

    /// <summary>The type byte of a register that takes any value.</summary>
    public const byte AnyType = byte.MaxValue;

    private const byte Format = 1;
    private const int MaxText = 1 << 20;

    private static readonly byte[] Magic = "TKPL"u8.ToArray();

    public required string Name { get; init; }

    /// <summary>Gets how many of the first registers are the parameters.</summary>
    public required int Parameters { get; init; }

    /// <summary>Gets the type of the result, or <see cref="AnyType"/> if the procedure returns nothing.</summary>
    public required byte Returns { get; init; }

    /// <summary>Gets the name of each register: a variable, a column of a loop row (<c>row.column</c>), or empty for a temporary.</summary>
    public required string[] RegisterNames { get; init; }

    /// <summary>Gets the type each register converts what is assigned to it, or <see cref="AnyType"/>.</summary>
    public required byte[] RegisterTypes { get; init; }

    public required (PlConstant Kind, string Text)[] Constants { get; init; }

    public required byte[] Code { get; init; }

    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        stream.Write(Magic);
        stream.WriteByte(Format);
        Write(stream, Name);
        Write(stream, (ushort)Parameters);
        stream.WriteByte(Returns);
        Write(stream, (ushort)RegisterNames.Length);
        for (var i = 0; i < RegisterNames.Length; i++)
        {
            Write(stream, RegisterNames[i]);
            stream.WriteByte(RegisterTypes[i]);
        }

        Write(stream, (ushort)Constants.Length);
        foreach (var (kind, text) in Constants)
        {
            stream.WriteByte((byte)kind);
            Write(stream, text);
        }

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, Code.Length);
        stream.Write(length);
        stream.Write(Code);
        return stream.ToArray();
    }

    /// <summary>Reads a program and checks it: one that comes back from here cannot make the machine read outside its registers, its constants or its code.</summary>
    /// <exception cref="TkSqlException">The bytes are not a program this build can run.</exception>
    public static PlProgram Decode(ReadOnlySpan<byte> bytes, string name)
    {
        try
        {
            var reader = new Reader(bytes);
            if (!reader.Take(Magic.Length).SequenceEqual(Magic))
            {
                throw new InvalidDataException("it does not start as a procedure does");
            }

            var format = reader.Byte();
            if (format != Format)
            {
                throw new InvalidDataException($"it is of format {format}, and this build runs format {Format}");
            }

            var stored = reader.Text();
            var parameters = reader.UInt16();
            var returns = reader.Byte();
            var registers = reader.UInt16();
            var names = new string[registers];
            var types = new byte[registers];
            for (var i = 0; i < registers; i++)
            {
                names[i] = reader.Text();
                types[i] = reader.Byte();
                if (types[i] != AnyType && !Enum.IsDefined((TkColumnType)types[i]))
                {
                    throw new InvalidDataException($"register {i} has unknown type {types[i]}");
                }
            }

            var constants = new (PlConstant Kind, string Text)[reader.UInt16()];
            for (var i = 0; i < constants.Length; i++)
            {
                var kind = reader.Byte();
                constants[i] = kind <= (byte)PlConstant.Name
                    ? ((PlConstant)kind, reader.Text())
                    : throw new InvalidDataException($"constant {i} is of unknown kind {kind}");
            }

            var code = reader.Take(reader.Int32()).ToArray();
            if (!reader.AtEnd || parameters > registers || (returns != AnyType && !Enum.IsDefined((TkColumnType)returns)))
            {
                throw new InvalidDataException("its header does not add up");
            }

            var program = new PlProgram
            {
                Name = stored, Parameters = parameters, Returns = returns, RegisterNames = names, RegisterTypes = types, Constants = constants, Code = code,
            };
            program.Verify();
            return program;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or DecoderFallbackException)
        {
            throw new TkSqlException($"The stored procedure '{name}' cannot be run: {exception.Message}.", exception);
        }
    }

    public static ushort UInt16(byte[] code, int at) => BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(at, 2));

    public static int Int32(byte[] code, int at) => BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(at, 4));

    /// <summary>Every instruction is known and whole, names registers and constants that exist and of the right kind, and jumps to the start of an instruction.</summary>
    private void Verify()
    {
        var starts = new HashSet<int>();
        var jumps = new List<int>();
        var at = 0;
        while (at < Code.Length)
        {
            starts.Add(at);
            var op = (PlOp)Code[at++];
            switch (op)
            {
                case PlOp.Evaluate:
                    Register(Operand());
                    Constant(Operand(), PlConstant.Expression);
                    break;
                case PlOp.Jump:
                    jumps.Add(Target());
                    break;
                case PlOp.JumpUnless:
                    Register(Operand());
                    jumps.Add(Target());
                    break;
                case PlOp.Execute:
                    Constant(Operand(), PlConstant.Statement);
                    break;
                case PlOp.Open:
                    Register(Operand());
                    Constant(Operand(), PlConstant.Statement);
                    break;
                case PlOp.Next:
                    Register(Operand());
                    var first = Operand();
                    var count = Operand();
                    if (first + count > RegisterNames.Length)
                    {
                        throw new InvalidDataException("a loop names registers it does not have");
                    }

                    jumps.Add(Target());
                    break;
                case PlOp.Into:
                    Constant(Operand(), PlConstant.Statement);
                    for (var n = Operand(); n > 0; n--)
                    {
                        Register(Operand());
                    }

                    break;
                case PlOp.Call:
                    Constant(Operand(), PlConstant.Name);
                    if (Operand() is var result && result != None)
                    {
                        Register(result);
                    }

                    for (var n = Operand(); n > 0; n--)
                    {
                        Register(Operand());
                    }

                    break;
                case PlOp.Return:
                    if (Operand() is var value && value != None)
                    {
                        Register(value);
                    }

                    break;
                case PlOp.Raise:
                    Register(Operand());
                    break;
                default:
                    throw new InvalidDataException($"it has an instruction this build does not know ({(byte)op})");
            }
        }

        if (jumps.Exists(target => target != Code.Length && !starts.Contains(target)))
        {
            throw new InvalidDataException("it jumps into the middle of an instruction");
        }

        ushort Operand()
        {
            if (at + 2 > Code.Length)
            {
                throw new InvalidDataException("its code ends in the middle of an instruction");
            }

            at += 2;
            return UInt16(Code, at - 2);
        }

        int Target()
        {
            if (at + 4 > Code.Length)
            {
                throw new InvalidDataException("its code ends in the middle of an instruction");
            }

            at += 4;
            return Int32(Code, at - 4);
        }

        void Register(ushort register)
        {
            if (register >= RegisterNames.Length)
            {
                throw new InvalidDataException("it names a register it does not have");
            }
        }

        void Constant(ushort constant, PlConstant kind)
        {
            if (constant >= Constants.Length || Constants[constant].Kind != kind)
            {
                throw new InvalidDataException("it names a constant it does not have");
            }
        }
    }

    private static void Write(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void Write(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _at;

        public readonly bool AtEnd => _at == _bytes.Length;

        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _bytes.Length - _at)
            {
                throw new InvalidDataException("it is cut short");
            }

            _at += count;
            return _bytes.Slice(_at - count, count);
        }

        public byte Byte() => Take(1)[0];

        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public string Text()
        {
            var length = Int32();
            return length <= MaxText ? Strict.GetString(Take(length)) : throw new InvalidDataException("a text in it is too long");
        }
    }
}
