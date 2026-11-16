// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.AotSmoke;

internal enum Mood : byte
{
    Calm,
    Busy,
}

/// <summary>La primera forma de la tabla.</summary>
[TkTable("events")]
internal sealed partial class EventV1
{
    [TkKey]
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }

    public int Obsolete { get; set; }
}

/// <summary>
/// La misma tabla después de cambiar: una columna menos y varias más, de tipos distintos, y con índices
/// de cada clase: de columna, sobre una expresión y parcial.
/// </summary>
[TkTable("events")]
[TkIndex(nameof(Cost), Name = "busy", Where = nameof(IsBusy))]
internal sealed partial class EventV2
{
    [TkKey]
    public Guid Id { get; set; }

    [TkIndex(Unique = true)]
    public string Title { get; set; } = string.Empty;

    [TkIndex]
    public DateTimeOffset At { get; set; }

    [TkCheck]
    public bool CostIsNotNegative => Cost is null || Cost >= 0;

    [TkIndex]
    public int TitleLength => Title.Length;

    public bool IsBusy => Mood == Mood.Busy;

    [TkForeignKey(typeof(Venue))]
    public string? Place { get; set; }

    public decimal? Cost { get; set; }

    public Mood Mood { get; set; }

    public byte[]? Attachment { get; set; }

    public double Weight { get; set; }
}

/// <summary>La tabla a la que se refiere la clave foránea de los eventos.</summary>
[TkTable("venues")]
internal sealed partial class Venue
{
    [TkKey]
    public string Name { get; set; } = string.Empty;
}
