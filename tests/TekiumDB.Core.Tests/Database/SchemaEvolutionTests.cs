// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Database;

/// <summary>
/// El tipo de una tabla cambia con el tiempo y las filas ya escritas no se tocan: cada una dice con qué
/// versión del esquema se escribió y el catálogo sabe qué columnas tenía esa versión.
/// </summary>
public abstract class SchemaEvolutionTests : DatabaseTests
{
    private static readonly Guid Ana = Guid.Parse("01940000-0000-7000-8000-000000000001");
    private static readonly Guid Luis = Guid.Parse("01940000-0000-7000-8000-000000000002");
    private static readonly Guid Eva = Guid.Parse("01940000-0000-7000-8000-000000000003");

    /// <summary>Criterio de salida de la Fase 5: una fila escrita con la versión 1 se lee con el lector de la versión 2.</summary>
    [Fact]
    public async Task A_row_written_with_version_1_is_read_with_the_reader_of_version_2()
    {
        var device = NewDevice();
        await using (var database = await OpenAsync(device))
        {
            await WriteVersion1Async(database);
        }

        await using var reopened = await OpenAsync(device);
        var writes = device.WriteCount;
        var contacts = reopened.Table<ContactV2>();

        // Name llega por su nombre guardado, Legacy se salta, y lo que no existía sale con su valor por defecto.
        Assert.Equal(
            new ContactV2 { Id = Ana, FullName = "Ana", Phone = "555-0101", Email = null, Score = 0, Since = null, Notes = string.Empty },
            await contacts.GetAsync(Ana, Ct));
        Assert.Equal(
            [new ContactV2 { Id = Ana, FullName = "Ana", Phone = "555-0101" }, new ContactV2 { Id = Luis, FullName = "Luis", Phone = null }],
            await ToListAsync(contacts.ScanAsync(Ct)));

        // Leer con el tipo nuevo no reescribe ni una página.
        Assert.Equal(writes, device.WriteCount);
    }

    [Fact]
    public async Task Old_and_new_rows_live_together_and_each_type_reads_both()
    {
        var device = NewDevice();
        await using (var database = await OpenAsync(device))
        {
            await WriteVersion1Async(database);
            await using var writer = await database.BeginWriteAsync(Ct);
            var contacts = database.Table<ContactV2>();
            await contacts.InsertAsync(
                writer,
                new ContactV2 { Id = Eva, FullName = "Eva", Email = "eva@example.com", Score = 7, Since = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), Notes = "new" },
                Ct);

            // Dentro de la misma transacción, antes del commit, la versión nueva ya se puede leer.
            Assert.Equal("eva@example.com", (await contacts.GetAsync(writer, Eva, Ct))!.Email);
            Assert.Equal("Ana", (await contacts.GetAsync(writer, Ana, Ct))!.FullName);
            await writer.CommitAsync(Ct);
        }

        await using var reopened = await OpenAsync(device);
        var asNew = await ToListAsync(reopened.Table<ContactV2>().ScanAsync(Ct));
        Assert.Equal(["Ana", "Luis", "Eva"], asNew.Select(static contact => contact.FullName));
        Assert.Equal([0, 0, 7], asNew.Select(static contact => contact.Score));
        Assert.Equal(DateTimeKind.Utc, asNew[2].Since!.Value.Kind);

        // El tipo viejo también lee la fila nueva: ignora las columnas que no conoce.
        var asOld = await ToListAsync(reopened.Table<ContactV1>().ScanAsync(Ct));
        Assert.Equal(
            [
                new ContactV1 { Id = Ana, Name = "Ana", Legacy = 1, Phone = "555-0101" },
                new ContactV1 { Id = Luis, Name = "Luis", Legacy = 2, Phone = null },
                new ContactV1 { Id = Eva, Name = "Eva", Legacy = 0, Phone = null },
            ],
            asOld);
        await AssertNoLeaksAsync(reopened);
    }

    [Fact]
    public async Task Writing_again_with_the_same_type_does_not_add_schema_versions()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        await WriteVersion1Async(database);
        for (var i = 0; i < 3; i++)
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            await database.Table<ContactV1>().UpsertAsync(writer, new ContactV1 { Id = Ana, Name = "Ana", Legacy = i }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(1, await CountSchemaVersionsAsync(database));

        // Y con el tipo nuevo, una más, una sola vez.
        for (var i = 0; i < 3; i++)
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            await database.Table<ContactV2>().UpsertAsync(writer, new ContactV2 { Id = Ana, FullName = "Ana", Score = i }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(2, await CountSchemaVersionsAsync(database));
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task A_schema_version_from_a_transaction_that_rolled_back_leaves_no_trace()
    {
        await using var database = await OpenAsync(NewDevice());
        await WriteVersion1Async(database);
        await using (var abandoned = await database.BeginWriteAsync(Ct))
        {
            await database.Table<ContactV2>().InsertAsync(abandoned, new ContactV2 { Id = Eva, FullName = "Eva", Score = 7 }, Ct);
            Assert.Equal(7, (await database.Table<ContactV2>().GetAsync(abandoned, Eva, Ct))!.Score);
        }

        // Otra forma del tipo toma el mismo número de versión, con otras columnas.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<ContactAlternative>().InsertAsync(writer, new ContactAlternative { Id = Eva, Name = "Eva", Score = long.MaxValue }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(
            new ContactAlternative { Id = Eva, Name = "Eva", Score = long.MaxValue },
            await database.Table<ContactAlternative>().GetAsync(Eva, Ct));
        Assert.Equal("Eva", (await database.Table<ContactV1>().GetAsync(Eva, Ct))!.Name);

        // El tipo que registró la versión descartada no la recuerda: lee la que quedó, que para él es
        // incompatible (Score es otro tipo), y lo dice en vez de leer la fila con columnas que no son.
        var stale = await Assert.ThrowsAsync<TkSchemaException>(
            async () => await database.Table<ContactV2>().GetAsync(Eva, Ct));
        Assert.Contains("'Score'", stale.Message, StringComparison.Ordinal);
        Assert.Equal("Ana", (await database.Table<ContactV2>().GetAsync(Ana, Ct))!.FullName);
    }

    [Fact]
    public async Task A_change_that_cannot_be_bridged_is_refused_naming_the_table_and_the_column()
    {
        await using var database = await OpenAsync(NewDevice());
        await WriteVersion1Async(database);

        // Cambiar el tipo de una columna.
        var anotherType = await Assert.ThrowsAsync<TkSchemaException>(
            async () => await database.Table<ContactWithAnotherType>().GetAsync(Ana, Ct));
        Assert.Contains("'contacts'", anotherType.Message, StringComparison.Ordinal);
        Assert.Contains("'Name'", anotherType.Message, StringComparison.Ordinal);

        // Cambiar la clave.
        var anotherKey = await Assert.ThrowsAsync<TkSchemaException>(
            async () => await ToListAsync(database.Table<ContactWithAnotherKey>().ScanAsync(Ct)));
        Assert.Contains("key", anotherKey.Message, StringComparison.Ordinal);

        // Declarar no anulable una columna que tiene nulos guardados.
        var forbidsNull = await Assert.ThrowsAsync<TkSchemaException>(
            async () => await database.Table<ContactThatForbidsNull>().GetAsync(Luis, Ct));
        Assert.Contains("'Phone'", forbidsNull.Message, StringComparison.Ordinal);

        // Tampoco se puede escribir: se rechaza antes de registrar nada.
        var before = database.Pager.Current;
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await Assert.ThrowsAsync<TkSchemaException>(
                async () => await database.Table<ContactWithAnotherType>().UpsertAsync(writer, new ContactWithAnotherType { Id = Ana, Name = 1 }, Ct));
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(before, database.Pager.Current);
        Assert.Equal("Ana", (await database.Table<ContactV1>().GetAsync(Ana, Ct))!.Name);
    }

    private static async Task WriteVersion1Async(TekiumDb database)
    {
        await using var writer = await database.BeginWriteAsync(Ct);
        var contacts = database.Table<ContactV1>();
        await contacts.InsertAsync(writer, new ContactV1 { Id = Ana, Name = "Ana", Legacy = 1, Phone = "555-0101" }, Ct);
        await contacts.InsertAsync(writer, new ContactV1 { Id = Luis, Name = "Luis", Legacy = 2, Phone = null }, Ct);
        await writer.CommitAsync(Ct);
    }
}

public sealed class SchemaEvolutionTests_File : SchemaEvolutionTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class SchemaEvolutionTests_Memory : SchemaEvolutionTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
