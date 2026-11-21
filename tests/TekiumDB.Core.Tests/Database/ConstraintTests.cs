// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Database;

/// <summary>
/// Restricciones declarativas. Criterio de la Fase 6.5: una violación aborta la transacción nombrando la
/// restricción y la fila, y el planificador usa UNIQUE para acotar el resultado a una fila.
/// </summary>
public abstract class ConstraintTests : DatabaseTests
{
    [Fact]
    public async Task A_row_that_fails_a_check_aborts_the_transaction_naming_the_constraint_and_the_row()
    {
        await using var database = await SeededAsync();
        var books = database.Table<Book>();

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await books.InsertAsync(writer, NewBook(7), Ct);
            var violation = await Assert.ThrowsAsync<TkConstraintException>(
                async () => await books.InsertAsync(writer, NewBook(8) with { Pages = 0 }, Ct));
            Assert.Equal("HasPages", violation.Constraint);
            Assert.Equal("books", violation.Table);
            Assert.Equal("8", violation.Key);
            Assert.Contains("CHECK constraint 'HasPages'", violation.Message, StringComparison.Ordinal);
            Assert.Contains("key 8", violation.Message, StringComparison.Ordinal);
            Assert.Contains("'books'", violation.Message, StringComparison.Ordinal);

            // La transacción quedó abortada: ni lo que hizo antes de la violación puede confirmarse.
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CommitAsync(Ct));
            Assert.Contains("rolled back", refused.Message, StringComparison.Ordinal);
        }

        Assert.Null(await books.GetAsync(7, Ct));
        Assert.Null(await books.GetAsync(8, Ct));

        // También al actualizar, y con el nombre que se le dio a la restricción.
        await InTransactionAsync(database, writer => books.InsertAsync(writer, NewBook(7), Ct));
        var onUpdate = await ViolationAsync(database, async writer => await books.UpdateAsync(writer, NewBook(7) with { Price = -0.01m }, Ct));
        Assert.Equal("price is not negative", onUpdate.Constraint);
        Assert.Equal("7", onUpdate.Key);
        Assert.Equal(NewBook(7), await books.GetAsync(7, Ct));
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Two_rows_cannot_have_the_same_values_in_a_unique_index()
    {
        await using var database = await SeededAsync();
        var authors = database.Table<Author>();
        var books = database.Table<Book>();

        var duplicate = await ViolationAsync(
            database, async writer => await authors.InsertAsync(writer, new Author { Handle = "impostor", Email = "ana@example.com" }, Ct));
        Assert.Equal("Email", duplicate.Constraint);
        Assert.Equal("authors", duplicate.Table);
        Assert.Equal("impostor", duplicate.Key);
        Assert.Contains("UNIQUE constraint 'Email'", duplicate.Message, StringComparison.Ordinal);

        // Sin valor no hay con qué chocar: cuantas filas se quiera. Y una fila no choca consigo misma.
        await InTransactionAsync(database, async writer =>
        {
            await authors.InsertAsync(writer, new Author { Handle = "anon1" }, Ct);
            await authors.InsertAsync(writer, new Author { Handle = "anon2" }, Ct);
            await authors.UpsertAsync(writer, new Author { Handle = "ana", Email = "ana@example.com", Name = "Ana María" }, Ct);
        });

        // Al actualizar hacia un valor que otro tiene, y entre dos filas de la misma transacción.
        Assert.Equal("Email", (await ViolationAsync(
            database, async writer => await authors.UpdateAsync(writer, new Author { Handle = "anon1", Email = "luis@example.com" }, Ct))).Constraint);
        Assert.Equal("anon2", (await ViolationAsync(database, async writer =>
        {
            await authors.UpsertAsync(writer, new Author { Handle = "anon1", Email = "new@example.com" }, Ct);
            await authors.UpsertAsync(writer, new Author { Handle = "anon2", Email = "new@example.com" }, Ct);
        })).Key);

        // Varias columnas: el mismo título vale para otro autor, no para el mismo.
        await InTransactionAsync(database, async writer =>
        {
            await books.InsertAsync(writer, NewBook(1) with { Title = "Dune" }, Ct);
            await books.InsertAsync(writer, NewBook(2) with { Title = "Dune", Author = "luis" }, Ct);
        });
        Assert.Equal("one title per author", (await ViolationAsync(
            database, async writer => await books.InsertAsync(writer, NewBook(3) with { Title = "Dune" }, Ct))).Constraint);

        // Quien deja libre un valor permite que otro lo tome, en la misma transacción.
        await InTransactionAsync(database, async writer =>
        {
            await books.DeleteAsync(writer, 1, Ct);
            await books.InsertAsync(writer, NewBook(3) with { Title = "Dune" }, Ct);
        });
        Assert.Equal(2, (await ToListAsync(books.ScanAsync(Ct))).Count(static book => book.Title == "Dune"));
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task A_partial_unique_index_only_constrains_the_rows_in_it()
    {
        await using var database = await SeededAsync();
        var books = database.Table<Book>();
        await InTransactionAsync(database, async writer =>
        {
            await books.InsertAsync(writer, NewBook(1) with { Isbn = "X", OutOfPrint = true }, Ct);
            await books.InsertAsync(writer, NewBook(2) with { Isbn = "X", OutOfPrint = true }, Ct);
            await books.InsertAsync(writer, NewBook(3) with { Isbn = "X" }, Ct);
        });

        var violation = await ViolationAsync(database, async writer => await books.InsertAsync(writer, NewBook(4) with { Isbn = "X" }, Ct));
        Assert.Equal("isbn in print", violation.Constraint);

        // Volver a imprimir uno de los agotados lo mete en el índice, donde ya hay otro con ese ISBN.
        violation = await ViolationAsync(database, async writer => await books.UpdateAsync(writer, NewBook(1) with { Isbn = "X" }, Ct));
        Assert.Equal("1", violation.Key);
    }

    [Fact]
    public async Task A_row_must_refer_to_one_that_exists_and_that_one_cannot_be_deleted_meanwhile()
    {
        await using var database = await SeededAsync();
        var authors = database.Table<Author>();
        var books = database.Table<Book>();

        var orphan = await ViolationAsync(database, async writer => await books.InsertAsync(writer, NewBook(1) with { Author = "nobody" }, Ct));
        Assert.Equal("fk_Author", orphan.Constraint);
        Assert.Equal("books", orphan.Table);
        Assert.Equal("1", orphan.Key);
        Assert.Contains("FOREIGN KEY constraint 'fk_Author'", orphan.Message, StringComparison.Ordinal);
        Assert.Contains("'authors'", orphan.Message, StringComparison.Ordinal);

        // Una clave de texto con un cero dentro se busca igual en la tabla y en el índice.
        await InTransactionAsync(database, async writer =>
        {
            await authors.InsertAsync(writer, new Author { Handle = "a\0b" }, Ct);
            await authors.InsertAsync(writer, new Author { Handle = "a" }, Ct);
            await books.InsertAsync(writer, NewBook(1) with { Author = "a\0b" }, Ct);
            await books.InsertAsync(writer, NewBook(2), Ct);
        });

        var referred = await ViolationAsync(database, async writer => await authors.DeleteAsync(writer, "a\0b", Ct));
        Assert.Equal("fk_Author", referred.Constraint);
        Assert.Equal("books", referred.Table);
        Assert.Equal("a\0b", referred.Key);
        Assert.Contains("'authors'", referred.Message, StringComparison.Ordinal);
        await ViolationAsync(database, async writer => await authors.DeleteAsync(writer, "ana", Ct));

        // A quien nadie se refiere se le puede borrar, y a los demás cuando ya nadie lo hace.
        await InTransactionAsync(database, async writer =>
        {
            Assert.True(await authors.DeleteAsync(writer, "a", Ct));
            Assert.True(await books.DeleteAsync(writer, 1, Ct));
            Assert.True(await authors.DeleteAsync(writer, "a\0b", Ct));
            Assert.False(await authors.DeleteAsync(writer, "never existed", Ct));
        });

        // Cambiar la referencia exige que la nueva exista.
        Assert.Equal("fk_Author", (await ViolationAsync(
            database, async writer => await books.UpdateAsync(writer, NewBook(2) with { Author = "a" }, Ct))).Constraint);
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task A_table_can_refer_to_itself()
    {
        await using var database = await SeededAsync();
        var books = database.Table<Book>();
        await InTransactionAsync(database, async writer =>
        {
            await books.InsertAsync(writer, NewBook(1), Ct);
            await books.InsertAsync(writer, NewBook(2) with { SequelOf = 1 }, Ct);

            // Una fila puede referirse a sí misma, aun al insertarse.
            await books.InsertAsync(writer, NewBook(3) with { SequelOf = 3 }, Ct);
        });

        Assert.Equal("sequel of", (await ViolationAsync(
            database, async writer => await books.InsertAsync(writer, NewBook(4) with { SequelOf = 99 }, Ct))).Constraint);
        var referred = await ViolationAsync(database, async writer => await books.DeleteAsync(writer, 1, Ct));
        Assert.Equal("sequel of", referred.Constraint);
        Assert.Equal("1", referred.Key);

        await InTransactionAsync(database, async writer =>
        {
            Assert.True(await books.DeleteAsync(writer, 3, Ct));
            Assert.True(await books.DeleteAsync(writer, 2, Ct));
            Assert.True(await books.DeleteAsync(writer, 1, Ct));
        });
        Assert.Empty(await ToListAsync(books.ScanAsync(Ct)));
    }

    /// <summary>Criterio de la Fase 6.5: el planificador usa UNIQUE para acotar el resultado a una fila.</summary>
    [Fact]
    public async Task The_planner_knows_a_unique_index_gives_at_most_one_row()
    {
        await using var database = await SeededAsync();
        var authors = database.Table<Author>();
        var books = database.Table<Book>();
        await InTransactionAsync(database, async writer =>
        {
            await authors.InsertAsync(writer, new Author { Handle = "anon1" }, Ct);
            await authors.InsertAsync(writer, new Author { Handle = "anon2" }, Ct);
            for (var i = 1; i <= 50; i++)
            {
                await books.InsertAsync(writer, NewBook(i) with { OutOfPrint = i % 2 == 0, Isbn = $"isbn-{i / 2}" }, Ct);
            }
        });

        var byEmail = Author.Fields.Email.EqualTo("luis@example.com");
        Assert.Equal(new TkPlan(TkAccess.UniqueLookup, "Email"), await authors.ExplainAsync(byEmail, Ct));
        Assert.Equal("luis", Assert.Single(await ToListAsync(authors.QueryAsync(byEmail, Ct))).Handle);
        Assert.Empty(await ToListAsync(authors.QueryAsync(Author.Fields.Email.EqualTo("nobody@example.com"), Ct)));
        Assert.Empty(await ToListAsync(authors.QueryAsync(byEmail.And(Author.Fields.Name.EqualTo("someone else")), Ct)));

        // Las filas sin valor no están sujetas a la restricción: pueden ser muchas.
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "Email"), await authors.ExplainAsync(Author.Fields.Email.IsNull(), Ct));
        Assert.Equal(2, (await ToListAsync(authors.QueryAsync(Author.Fields.Email.IsNull(), Ct))).Count);
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "Email"), await authors.ExplainAsync(Author.Fields.Email.AtLeast("a"), Ct));

        // Con todas las columnas fijadas es una fila; con sólo la primera, un rango.
        var byTitle = Book.Fields.Author.EqualTo("ana").And(Book.Fields.Title.EqualTo("Book 9"));
        Assert.Equal(new TkPlan(TkAccess.UniqueLookup, "one title per author"), await books.ExplainAsync(byTitle, Ct));
        Assert.Equal(9, Assert.Single(await ToListAsync(books.QueryAsync(byTitle, Ct))).Id);
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "one title per author"), await books.ExplainAsync(Book.Fields.Author.EqualTo("ana"), Ct));
        Assert.Equal(50, (await ToListAsync(books.QueryAsync(Book.Fields.Author.EqualTo("ana"), Ct))).Count);

        // El único parcial sólo acota si la consulta implica su condición.
        var inPrint = Book.Fields.Isbn.EqualTo("isbn-4").And(Book.Fields.OutOfPrint.EqualTo(false));
        Assert.Equal(new TkPlan(TkAccess.UniqueLookup, "isbn in print"), await books.ExplainAsync(inPrint, Ct));
        Assert.Equal(9, Assert.Single(await ToListAsync(books.QueryAsync(inPrint, Ct))).Id);
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await books.ExplainAsync(Book.Fields.Isbn.EqualTo("isbn-4"), Ct));
        Assert.Equal(2, (await ToListAsync(books.QueryAsync(Book.Fields.Isbn.EqualTo("isbn-4"), Ct))).Count);

        // La clave primaria sigue ganando.
        Assert.Equal(new TkPlan(TkAccess.KeyLookup, null), await authors.ExplainAsync(byEmail.And(Author.Fields.Handle.EqualTo("luis")), Ct));
    }

    [Fact]
    public async Task Constraints_added_to_a_table_are_checked_against_the_rows_it_already_has()
    {
        async Task<TkConstraintException> AddingToAsync(params BookLoose[] rows)
        {
            await using var database = await SeededAsync();
            await InTransactionAsync(database, async writer =>
            {
                foreach (var row in rows)
                {
                    await database.Table<BookLoose>().InsertAsync(writer, row, Ct);
                }
            });

            // Leer con el tipo nuevo no comprueba nada; la primera escritura sí, aunque sea de otra fila.
            Assert.Equal(rows.Length, (await ToListAsync(database.Table<Book>().ScanAsync(Ct))).Count);
            var violation = await ViolationAsync(database, async writer => await database.Table<Book>().InsertAsync(writer, NewBook(100), Ct));
            Assert.Equal(rows.Length, (await ToListAsync(database.Table<BookLoose>().ScanAsync(Ct))).Count);
            await AssertNoLeaksAsync(database);
            return violation;
        }

        var good = new BookLoose { Id = 1, Author = "ana", Title = "Fine", Isbn = "1" };
        var check = await AddingToAsync(good, good with { Id = 2, Title = "No pages", Isbn = "2", Pages = 0 });
        Assert.Equal(("HasPages", "2"), (check.Constraint, check.Key));

        var orphan = await AddingToAsync(good, good with { Id = 3, Title = "Orphan", Isbn = "3", Author = "nobody" });
        Assert.Equal(("fk_Author", "3"), (orphan.Constraint, orphan.Key));

        var sequel = await AddingToAsync(good, good with { Id = 4, Title = "Sequel", Isbn = "4", SequelOf = 44 });
        Assert.Equal(("sequel of", "4"), (sequel.Constraint, sequel.Key));

        var duplicate = await AddingToAsync(good, good with { Id = 5, Isbn = "5" });
        Assert.Equal(("one title per author", "5"), (duplicate.Constraint, duplicate.Key));

        var isbn = await AddingToAsync(good, good with { Id = 6, Title = "Same isbn" });
        Assert.Equal(("isbn in print", "6"), (isbn.Constraint, isbn.Key));
    }

    [Fact]
    public async Task Constraints_follow_the_type_that_writes_once_added_they_hold_and_once_dropped_they_are_gone()
    {
        var device = NewDevice();
        await using (var database = await SeededAsync(device))
        {
            await InTransactionAsync(database, writer => database.Table<BookLoose>().InsertAsync(writer, new BookLoose { Id = 1, Author = "ana", Title = "Old" }, Ct));
            await InTransactionAsync(database, writer => database.Table<Book>().InsertAsync(writer, NewBook(2), Ct));
        }

        await using var reopened = await OpenAsync(device);
        var authors = reopened.Table<Author>();
        var books = reopened.Table<Book>();

        // Añadidas en la sesión anterior, siguen ahí: también para la fila que escribió el tipo viejo.
        Assert.Equal("fk_Author", (await ViolationAsync(reopened, async writer => await authors.DeleteAsync(writer, "ana", Ct))).Constraint);
        Assert.Equal("HasPages", (await ViolationAsync(reopened, async writer => await books.UpdateAsync(writer, NewBook(1) with { Pages = -1 }, Ct))).Constraint);

        // Quien escribe con un tipo que no las declara las quita: no podría mantenerlas.
        await InTransactionAsync(reopened, async writer =>
        {
            await reopened.Table<BookLoose>().UpsertAsync(writer, new BookLoose { Id = 3, Author = "nobody", Title = "Loose", Isbn = "loose", Pages = 0 }, Ct);
            Assert.True(await authors.DeleteAsync(writer, "ana", Ct));
        });
        Assert.Null(await authors.GetAsync("ana", Ct));
        await AssertNoLeaksAsync(reopened);

        // Y quien vuelve a declararlas las vuelve a tener: primero contra lo que hay, después en cada escritura.
        var stale = await ViolationAsync(reopened, async writer => await books.InsertAsync(writer, NewBook(4) with { Author = "luis" }, Ct));
        Assert.Equal(("fk_Author", "1"), (stale.Constraint, stale.Key));
        await InTransactionAsync(reopened, async writer =>
        {
            await reopened.Table<BookLoose>().DeleteAsync(writer, 3, Ct);
            await reopened.Table<BookLoose>().DeleteAsync(writer, 1, Ct);
            await reopened.Table<BookLoose>().DeleteAsync(writer, 2, Ct);
        });
        await InTransactionAsync(reopened, writer => books.InsertAsync(writer, NewBook(4) with { Author = "luis" }, Ct));
        Assert.Equal("fk_Author", (await ViolationAsync(reopened, async writer => await authors.DeleteAsync(writer, "luis", Ct))).Constraint);
        await AssertNoLeaksAsync(reopened);
    }

    [Fact]
    public async Task An_index_that_becomes_unique_is_rebuilt_and_refuses_the_duplicates_it_finds()
    {
        await using var database = await SeededAsync();
        var loose = database.Table<AuthorLoose>();
        var authors = database.Table<Author>();

        // Con el tipo que no lo exige, el índice del mismo nombre deja de ser único y admite repetidos.
        await InTransactionAsync(database, writer => loose.InsertAsync(writer, new AuthorLoose { Handle = "twin", Email = "ana@example.com" }, Ct));
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "Email"), await loose.ExplainAsync(AuthorLoose.Fields.Email.EqualTo("ana@example.com"), Ct));
        Assert.Equal(2, (await ToListAsync(loose.QueryAsync(AuthorLoose.Fields.Email.EqualTo("ana@example.com"), Ct))).Count);

        // El que hay no es el índice único que este tipo declara: no puede fiarse de él para devolver una sola fila.
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await authors.ExplainAsync(Author.Fields.Email.EqualTo("ana@example.com"), Ct));
        Assert.Equal(2, (await ToListAsync(authors.QueryAsync(Author.Fields.Email.EqualTo("ana@example.com"), Ct))).Count);

        var duplicate = await ViolationAsync(database, async writer => await authors.InsertAsync(writer, new Author { Handle = "new" }, Ct));
        Assert.Equal(("Email", "twin"), (duplicate.Constraint, duplicate.Key));

        await InTransactionAsync(database, async writer => await loose.DeleteAsync(writer, "twin", Ct));
        await InTransactionAsync(database, writer => authors.InsertAsync(writer, new Author { Handle = "new" }, Ct));
        Assert.Equal(new TkPlan(TkAccess.UniqueLookup, "Email"), await authors.ExplainAsync(Author.Fields.Email.EqualTo("ana@example.com"), Ct));
        await AssertNoLeaksAsync(database);
    }

    /// <summary>Operaciones al azar, cada una en su transacción, contra un modelo que aplica las mismas reglas.</summary>
    [Fact]
    public async Task Random_writes_are_accepted_or_refused_exactly_as_the_rules_say()
    {
        await using var database = await OpenAsync(NewDevice());
        var authors = database.Table<Author>();
        var books = database.Table<Book>();
        var modelAuthors = new SortedDictionary<string, Author>(StringComparer.Ordinal);
        var modelBooks = new SortedDictionary<int, Book>();
        var random = new Random(65);
        string[] handles = ["ana", "luis", "eva", "a\0", "ñ"];
        var refused = 0;

        for (var step = 0; step < 1500; step++)
        {
            string? expected = null;
            Func<TkWriteTransaction, ValueTask> operation;
            Action apply;
            var handle = handles[random.Next(handles.Length)];
            var id = random.Next(12);
            switch (random.Next(6))
            {
                case 0:
                    var author = new Author { Handle = handle, Email = random.Next(3) == 0 ? null : $"mail{random.Next(4)}", Name = $"n{step}" };
                    expected = author.Email is not null && modelAuthors.Values.Any(other => other.Email == author.Email && other.Handle != handle) ? "Email" : null;
                    operation = writer => authors.UpsertAsync(writer, author, Ct);
                    apply = () => modelAuthors[handle] = author;
                    break;
                case 1:
                    expected = modelBooks.Values.Any(book => book.Author == handle) ? "fk_Author" : null;
                    operation = async writer => await authors.DeleteAsync(writer, handle, Ct);
                    apply = () => modelAuthors.Remove(handle);
                    break;
                case 2:
                    expected = modelBooks.Values.Any(book => book.SequelOf == id && book.Id != id) ? "sequel of" : null;
                    operation = async writer => await books.DeleteAsync(writer, id, Ct);
                    apply = () => modelBooks.Remove(id);
                    break;
                default:
                    var book = new Book
                    {
                        Id = id,
                        Author = handle,
                        SequelOf = random.Next(3) == 0 ? random.Next(12) : null,
                        Title = $"t{random.Next(6)}",
                        Isbn = $"i{random.Next(6)}",
                        OutOfPrint = random.Next(3) == 0,
                        Pages = random.Next(10) == 0 ? 0 : 100,
                        Price = random.Next(10) == 0 ? -1 : 5,
                    };
                    var others = modelBooks.Values.Where(other => other.Id != id).ToList();

                    // En el orden en que el motor las comprueba: CHECK, FOREIGN KEY, UNIQUE.
                    expected =
                        !book.HasPages ? "HasPages"
                        : !book.PriceIsValid ? "price is not negative"
                        : !modelAuthors.ContainsKey(handle) ? "fk_Author"
                        : book.SequelOf is { } sequel && sequel != id && !modelBooks.ContainsKey(sequel) ? "sequel of"
                        : others.Any(other => other.Author == book.Author && other.Title == book.Title) ? "one title per author"
                        : book.InPrint && others.Any(other => other.InPrint && other.Isbn == book.Isbn) ? "isbn in print"
                        : null;
                    operation = writer => books.UpsertAsync(writer, book, Ct);
                    apply = () => modelBooks[id] = book;
                    break;
            }

            if (expected is null)
            {
                await InTransactionAsync(database, operation);
                apply();
            }
            else
            {
                refused++;
                Assert.Equal(expected, (await ViolationAsync(database, operation)).Constraint);
            }
        }

        Assert.Equal(modelAuthors.Values, await ToListAsync(authors.ScanAsync(Ct)));
        Assert.Equal(modelBooks.Values, await ToListAsync(books.ScanAsync(Ct)));
        Assert.True(refused > 200 && refused < 1300, $"{refused} of 1500 writes were refused: the test is not exercising both outcomes.");
        await AssertNoLeaksAsync(database);
    }

    private static Book NewBook(int id) => new() { Id = id, Author = "ana", Title = $"Book {id}", Isbn = $"isbn-{id}", Pages = 100, Price = 9.99m };

    /// <summary>Una base con dos autores.</summary>
    private async Task<TekiumDb> SeededAsync(TestDevice? device = null)
    {
        var database = await OpenAsync(device ?? NewDevice());
        await InTransactionAsync(database, async writer =>
        {
            await database.Table<Author>().InsertAsync(writer, new Author { Handle = "ana", Email = "ana@example.com", Name = "Ana" }, Ct);
            await database.Table<Author>().InsertAsync(writer, new Author { Handle = "luis", Email = "luis@example.com", Name = "Luis" }, Ct);
        });
        return database;
    }

    private static async Task InTransactionAsync(TekiumDb database, Func<TkWriteTransaction, ValueTask> work)
    {
        await using var writer = await database.BeginWriteAsync(Ct);
        await work(writer);
        await writer.CommitAsync(Ct);
    }

    /// <summary>Corre algo que debe violar una restricción, y comprueba que la transacción quedó abortada.</summary>
    private static async Task<TkConstraintException> ViolationAsync(TekiumDb database, Func<TkWriteTransaction, ValueTask> work)
    {
        await using var writer = await database.BeginWriteAsync(Ct);
        var violation = await Assert.ThrowsAsync<TkConstraintException>(async () => await work(writer));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CommitAsync(Ct));
        return violation;
    }
}

public sealed class ConstraintTests_File : ConstraintTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class ConstraintTests_Memory : ConstraintTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
