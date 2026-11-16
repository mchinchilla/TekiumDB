// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Tests.Database;

[TkTable("authors")]
public sealed partial record Author
{
    [TkKey]
    public string Handle { get; set; } = string.Empty;

    [TkIndex(Unique = true)]
    public string? Email { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>Una restricción de cada clase: dos CHECK, dos UNIQUE (uno parcial) y dos FOREIGN KEY (una a su propia tabla).</summary>
[TkTable("books")]
[TkIndex(nameof(Author), nameof(Title), Unique = true, Name = "one title per author")]
[TkIndex(nameof(Isbn), Unique = true, Name = "isbn in print", Where = nameof(InPrint))]
public sealed partial record Book
{
    [TkKey]
    public int Id { get; set; }

    [TkForeignKey(typeof(Author))]
    public string Author { get; set; } = string.Empty;

    [TkForeignKey(typeof(Book), Name = "sequel of")]
    public int? SequelOf { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Isbn { get; set; } = string.Empty;

    public bool OutOfPrint { get; set; }

    public int Pages { get; set; } = 1;

    public decimal Price { get; set; }

    public bool InPrint => !OutOfPrint;

    [TkCheck]
    public bool HasPages => Pages > 0;

    [TkCheck(Name = "price is not negative")]
    public bool PriceIsValid => Price >= 0;
}

/// <summary>La tabla "books" sin ninguna restricción: como la escribiría una versión anterior de la aplicación.</summary>
[TkTable("books")]
public sealed partial record BookLoose
{
    [TkKey]
    public int Id { get; set; }

    public string Author { get; set; } = string.Empty;

    public int? SequelOf { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Isbn { get; set; } = string.Empty;

    public bool OutOfPrint { get; set; }

    public int Pages { get; set; } = 1;

    public decimal Price { get; set; }
}

/// <summary>La tabla "authors" con el mismo índice sobre el correo, pero sin exigir que sea único.</summary>
[TkTable("authors")]
public sealed partial record AuthorLoose
{
    [TkKey]
    public string Handle { get; set; } = string.Empty;

    [TkIndex]
    public string? Email { get; set; }

    public string Name { get; set; } = string.Empty;
}
