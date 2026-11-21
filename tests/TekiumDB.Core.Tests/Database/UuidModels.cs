// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB.Tests.Database;

/// <summary>Un UUID v7 como clave, otro en un índice y otro, anulable, en una columna cualquiera.</summary>
[TkTable("parcels")]
public sealed partial record Parcel
{
    [TkKey]
    public Guid Id { get; set; }

    [TkIndex]
    public Guid Order { get; set; }

    public Guid? Batch { get; set; }
}
