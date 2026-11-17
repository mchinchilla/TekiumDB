// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Tests.Sql;

/// <summary>Una tabla que SQL puede escribir entera: índices sólo sobre columnas (uno compuesto, uno único), sin restricciones.</summary>
[TkTable("items")]
[TkIndex(nameof(Owner), nameof(Qty))]
public sealed partial record Item
{
    [TkKey]
    public long Id { get; set; }

    [TkIndex]
    public string Owner { get; set; } = string.Empty;

    [TkIndex(Unique = true)]
    public string? Code { get; set; }

    public int Qty { get; set; }

    public decimal Price { get; set; }

    public DateTimeOffset? At { get; set; }

    public bool Active { get; set; }
}

/// <summary>Un índice parcial sobre una columna: sólo las tareas sin hacer. SQL lo mantiene, porque su condición compara una columna con un valor.</summary>
[TkTable("chores")]
[TkIndex(nameof(Due), Name = "pending", Where = nameof(IsPending))]
public sealed partial record Chore
{
    [TkKey]
    public long Id { get; set; }

    public int Due { get; set; }

    public bool Done { get; set; }

    public string Title { get; set; } = string.Empty;

    public bool IsPending => !Done;
}
