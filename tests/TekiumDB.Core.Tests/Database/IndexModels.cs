// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB.Tests.Database;

public enum TicketState : byte
{
    Open,
    Closed,
    Archived = 9,
}

/// <summary>Un índice de cada clase: de una columna, de varias, sobre una expresión y parcial.</summary>
[TkTable("tickets")]
[TkIndex(nameof(Owner), nameof(Priority))]
[TkIndex(nameof(Priority), Name = "open", Where = nameof(IsOpen))]
public sealed partial record Ticket
{
    [TkKey]
    public long Id { get; set; }

    [TkIndex]
    public string Owner { get; set; } = string.Empty;

    public int Priority { get; set; }

    [TkIndex]
    public DateTimeOffset? Due { get; set; }

    public TicketState State { get; set; }

    public bool Deleted { get; set; }

    public decimal Cost { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    [TkIndex]
    public string TitleLower => Title.ToLowerInvariant();

    public bool IsOpen => !Deleted && State == TicketState.Open;
}

/// <summary>La tabla "gadgets" antes de tener índices.</summary>
[TkTable("gadgets")]
public sealed partial record GadgetPlain
{
    [TkKey]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Size { get; set; }
}

/// <summary>La misma tabla con un índice de columna y otro sobre una expresión.</summary>
[TkTable("gadgets")]
public sealed partial record GadgetIndexed
{
    [TkKey]
    public int Id { get; set; }

    [TkIndex]
    public string Name { get; set; } = string.Empty;

    public int Size { get; set; }

    [TkIndex]
    public int Bucket => Size / 10;
}

/// <summary>La misma tabla después de cambiar la expresión del índice.</summary>
[TkTable("gadgets")]
public sealed partial record GadgetReindexed
{
    [TkKey]
    public int Id { get; set; }

    [TkIndex]
    public string Name { get; set; } = string.Empty;

    public int Size { get; set; }

    [TkIndex]
    public int Bucket => Size / 100;
}
