// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB.Tests.Database;

/// <summary>Una serie de tiempo con dos zone maps: sobre un instante y sobre una fecha sin hora que puede faltar.</summary>
[TkTable("readings")]
public sealed partial record Reading
{
    [TkKey]
    public long Id { get; set; }

    [TkZoneMap]
    public DateTimeOffset At { get; set; }

    [TkZoneMap(BucketMinutes = 2880)]
    public DateOnly? Day { get; set; }

    public double Value { get; set; }
}

/// <summary>La misma tabla sin zone maps: como la escribiría una versión anterior.</summary>
[TkTable("readings")]
public sealed partial record ReadingPlain
{
    [TkKey]
    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    public DateOnly? Day { get; set; }

    public double Value { get; set; }
}

/// <summary>Zone map e índice sobre la misma fecha: el planificador tiene que elegir.</summary>
[TkTable("events_by_time")]
public sealed partial record TimedEvent
{
    [TkKey]
    public Guid Id { get; set; }

    [TkIndex]
    [TkZoneMap(BucketMinutes = 10)]
    public DateTime At { get; set; }

    public string Name { get; set; } = string.Empty;
}
