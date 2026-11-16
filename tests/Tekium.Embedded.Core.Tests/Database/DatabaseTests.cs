// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Catalog;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Security;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Tests.Indexing;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Database;

/// <summary>Base de las pruebas de la API pública, sobre archivo y sobre memoria.</summary>
public abstract class DatabaseTests : DeviceBackedTests
{
    // Páginas chicas y Argon2id al mínimo: árboles de varios niveles con pocas filas y aperturas rápidas.
    private protected static readonly DbOptions Options = new()
    {
        InMemory = true,
        PageSize = 4096,
        CacheSizeMb = 1,
        KeyDerivation = new Argon2Parameters(MemoryKiB: 64, Iterations: 1, Parallelism: 1),
    };

    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Abre (o crea) una base sobre un dispositivo de prueba, que sobrevive al cierre para poder reabrirla.</summary>
    private protected static ValueTask<TekiumDb> OpenAsync(TestDevice device, string? password = null, int changeRetention = 0) =>
        TekiumDb.OpenAsync(
            _ => device,
            new DbOptions
            {
                InMemory = true,
                PageSize = Options.PageSize,
                CacheSizeMb = Options.CacheSizeMb,
                KeyDerivation = Options.KeyDerivation,
                Password = password.AsMemory(),
                ChangeRetention = changeRetention,
            },
            Ct);

    /// <summary>
    /// Ninguna página se pierde: toda página es de cabecera, del catálogo, de alguna tabla o índice, o está en la
    /// free-list. De paso verifica la estructura de cada árbol.
    /// </summary>
    private protected static async Task AssertNoLeaksAsync(TekiumDb database)
    {
        var pager = database.Pager;
        using var read = pager.BeginRead();
        var pages = await BTreeVerifier.VerifyAsync(pager, read.Root, Ct);
        var roots = new List<PageNumber>();
        await foreach (var entry in BTree.RangeAsync(pager, read.Root, default, default, Ct))
        {
            if (CatalogStore.IsTableKey(entry.Key.Span))
            {
                roots.Add(TableInfo.Decode(entry.Value.Span).Root);
            }
            else if (CatalogStore.IsIndexKey(entry.Key.Span))
            {
                roots.Add(CatalogStore.IndexRoot(entry.Value.Span));
            }
        }

        foreach (var root in roots)
        {
            pages += await BTreeVerifier.VerifyAsync(pager, root, Ct);
        }

        Assert.Equal(
            read.Snapshot.PageCount,
            FileHeader.MetaPages + (uint)pages + (uint)pager.FreePageCount + (uint)pager.FreeListPageCount);
    }

    /// <summary>Cuántas versiones de esquema guarda el catálogo, entre todas las tablas.</summary>
    private protected static async Task<int> CountSchemaVersionsAsync(TekiumDb database)
    {
        using var read = database.Pager.BeginRead();
        var count = 0;
        await foreach (var entry in BTree.RangeAsync(database.Pager, read.Root, default, default, Ct))
        {
            count += CatalogStore.IsSchemaKey(entry.Key.Span) ? 1 : 0;
        }

        return count;
    }

    /// <summary>Cuántas entradas tiene un índice en el último commit, o -1 si el catálogo no lo conoce.</summary>
    private protected static async Task<int> CountIndexEntriesAsync(TekiumDb database, string table, string index)
    {
        var pager = database.Pager;
        using var read = pager.BeginRead();
        var key = CatalogStore.IndexKey(CatalogStore.TableKey(table), index);
        await foreach (var entry in BTree.RangeAsync(pager, read.Root, key, default, Ct))
        {
            return entry.Key.Span.SequenceEqual(key)
                ? await BTreeVerifier.CountAsync(pager, CatalogStore.IndexRoot(entry.Value.Span), Ct)
                : -1;
        }

        return -1;
    }

    private protected static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> rows)
    {
        var list = new List<T>();
        await foreach (var row in rows)
        {
            list.Add(row);
        }

        return list;
    }
}
