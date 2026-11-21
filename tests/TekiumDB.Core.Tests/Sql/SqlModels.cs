// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB.Tests.Sql;

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

/// <summary>Una tabla a la que otra se refiere, sin nada que SQL no sepa mantener.</summary>
[TkTable("teams")]
public sealed partial record Team
{
    [TkKey]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>Dos claves foráneas y nada más: una a otra tabla y una a la propia. SQL las comprueba igual que este tipo.</summary>
[TkTable("players")]
public sealed partial record Player
{
    [TkKey]
    public long Id { get; set; }

    [TkForeignKey(typeof(Team))]
    public int? Team { get; set; }

    [TkForeignKey(typeof(Player), Name = "captain")]
    public long? Captain { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>La tabla "players" con una columna más, como la deja un ALTER TABLE.</summary>
[TkTable("players")]
public sealed partial record PlayerWithNumber
{
    [TkKey]
    public long Id { get; set; }

    [TkForeignKey(typeof(Team))]
    public int? Team { get; set; }

    [TkForeignKey(typeof(Player), Name = "captain")]
    public long? Captain { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Number { get; set; }

    public string? Nick { get; set; }
}

/// <summary>La tabla "teams" después de renombrarla a "clubs" y su columna Name a Title, como la lee un tipo nuevo.</summary>
[TkTable("clubs")]
public sealed partial record Club
{
    [TkKey]
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;
}
