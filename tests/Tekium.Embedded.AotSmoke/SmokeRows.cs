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

/// <summary>La misma tabla después de cambiar: una columna menos y varias más, de tipos distintos.</summary>
[TkTable("events")]
internal sealed partial class EventV2
{
    [TkKey]
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }

    public string? Place { get; set; }

    public decimal? Cost { get; set; }

    public Mood Mood { get; set; }

    public byte[]? Attachment { get; set; }

    public double Weight { get; set; }
}
