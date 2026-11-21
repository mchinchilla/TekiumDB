// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TekiumDB.EntityFrameworkCore;
using TekiumDB.Tests.Database;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.EntityFramework;

/// <summary>
/// El proveedor de EF Core. Cada cosa se hace dos veces, con el mismo modelo: sobre TekiumDB y sobre el
/// proveedor de SQLite, y tiene que dar lo mismo.
/// </summary>
public abstract class EfCoreTests : DatabaseTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_model_with_related_entities_is_created_saved_and_queried_like_on_sqlite()
    {
        await using var pair = await Pair.CreateAsync(await OpenAsync(NewDevice()));

        // Las tablas se crean una vez.
        await pair.SameAsync(static async db => [$"{await db.Database.EnsureCreatedAsync(Ct)}", $"{await db.Database.EnsureCreatedAsync(Ct)}"]);
        Assert.Equal(["True", "False"], pair.Last);

        // Altas: las claves enteras las pone la base, las Guid la aplicación; las relaciones, por navegación.
        await pair.SameAsync(static async db =>
        {
            var random = new Random(7);
            var authors = Enumerable.Range(0, 5).Select(static i => new Author { Name = $"author {i}", Email = i % 2 == 0 ? $"a{i}@example.com" : null }).ToList();
            db.AddRange(authors);
            for (var b = 0; b < 12; b++)
            {
                var blog = new Blog { Name = $"{(char)('a' + (b % 5))}-blog {b}", Rating = random.Next(6), Notes = b % 3 == 0 ? null : $"note {b}" };
                for (var p = 0; p < b % 4; p++)
                {
                    blog.Posts.Add(new Post
                    {
                        Id = new Guid(b, (short)p, 0, 0, 0, 0, 0, 0, 0, 0, 1),
                        Title = $"post {b}.{p}",
                        Likes = random.Next(40),
                        Published = random.Next(2) == 0,
                        Archived = random.Next(3) == 0,
                        At = Epoch.AddHours(random.Next(500)),
                        Author = authors[random.Next(authors.Count)],
                    });
                }

                db.Add(blog);
            }

            var saved = await db.SaveChangesAsync(Ct);
            return [$"{saved}", .. db.ChangeTracker.Entries<Blog>().Select(static e => $"{e.Entity.Id}:{e.Entity.Name}").Order(StringComparer.Ordinal)];
        });
        Assert.Equal("35", pair.Last[0]);

        // Where, OrderBy, Skip y Take.
        await pair.SameAsync(static async db => Show(await db.Blogs.Where(static b => b.Rating > 1).OrderBy(static b => b.Name).Skip(2).Take(4).ToListAsync(Ct)));
        await pair.SameAsync(static async db => Show(await db.Blogs.OrderByDescending(static b => b.Rating).ThenBy(static b => b.Id).ToListAsync(Ct)));
        await pair.SameAsync(static async db => Show(await db.Blogs.OrderBy(static b => b.Id).Skip(9).ToListAsync(Ct)));
        await pair.SameAsync(static async db => Show(await db.Blogs.Where(static b => b.Notes == null || b.Notes != "note 4").OrderBy(static b => b.Id).ToListAsync(Ct)));
        await pair.SameAsync(static async db => Show(await db.Posts.Where(static p => p.Published && !p.Archived).OrderBy(static p => p.Id).ToListAsync(Ct)));
        await pair.SameAsync(static async db =>
        {
            var wanted = new[] { 2, 3, 5, 99 };
            var least = 2;
            return Show(await db.Blogs.Where(b => wanted.Contains(b.Id) && b.Rating >= least).OrderBy(static b => b.Id).ToListAsync(Ct));
        });

        // Include: de una colección, de una referencia, encadenado, y junto con Take.
        await pair.SameAsync(static async db => Show(await db.Blogs.Include(static b => b.Posts).OrderBy(static b => b.Id).ToListAsync(Ct)));
        await pair.SameAsync(static async db => Show(await db.Blogs.OrderBy(static b => b.Id).Skip(1).Take(5).Include(static b => b.Posts).ThenInclude(static p => p.Author).ToListAsync(Ct)));
        await pair.SameAsync(static async db => Show(await db.Posts.Include(static p => p.Blog).Include(static p => p.Author).OrderBy(static p => p.Id).ToListAsync(Ct)));
        await pair.SameAsync(static async db => Show(await db.Authors.Include(static a => a.Posts).ThenInclude(static p => p.Blog).OrderBy(static a => a.Id).ToListAsync(Ct)));
        await pair.SameAsync(static async db => Show(await db.Blogs.AsSplitQuery().Include(static b => b.Posts).OrderBy(static b => b.Id).ToListAsync(Ct)));

        // Any, Count y First, solos y dentro de un Where.
        await pair.SameAsync(static async db =>
        [
            $"{await db.Blogs.AnyAsync(Ct)}",
            $"{await db.Blogs.AnyAsync(static b => b.Rating > 100, Ct)}",
            $"{await db.Posts.CountAsync(Ct)}",
            $"{await db.Posts.CountAsync(static p => p.Likes > 20, Ct)}",
            $"{await db.Posts.LongCountAsync(Ct)}",
            $"{(await db.Blogs.OrderBy(static b => b.Name).FirstAsync(Ct)).Name}",
            $"{(await db.Blogs.FirstOrDefaultAsync(static b => b.Rating > 100, Ct))?.Name}",
            $"{(await db.Blogs.SingleAsync(static b => b.Id == 3, Ct)).Name}",
            $"{(await db.Blogs.FindAsync([4], Ct))?.Name}",
            $"{await db.Posts.MaxAsync(static p => p.Likes, Ct)}",
            $"{await db.Posts.SumAsync(static p => p.Likes, Ct)}",
        ]);
        await pair.SameAsync(static async db => Show(await db.Blogs.Where(static b => b.Posts.Any(static p => p.Likes > 25)).OrderBy(static b => b.Id).ToListAsync(Ct)));
        await pair.SameAsync(static async db => Show(await db.Blogs.Where(static b => !b.Posts.Any()).OrderBy(static b => b.Id).ToListAsync(Ct)));
        await pair.SameAsync(static async db => Show(await db.Blogs.Where(static b => b.Posts.Count > 1).OrderBy(static b => b.Id).ToListAsync(Ct)));
        await pair.SameAsync(static async db => Show(await db.Authors.Where(static a => a.Posts.Any(static p => p.Blog!.Rating > 3)).OrderBy(static a => a.Id).ToListAsync(Ct)));

        // Proyecciones, con una navegación y con una cuenta.
        await pair.SameAsync(static async db =>
            (await db.Posts.Where(static p => p.Blog!.Rating > 2).OrderBy(static p => p.Id).Select(static p => new { p.Title, Blog = p.Blog!.Name, Author = p.Author!.Name }).ToListAsync(Ct))
            .ConvertAll(static row => $"{row.Title}|{row.Blog}|{row.Author}"));
        await pair.SameAsync(static async db =>
            (await db.Blogs.OrderBy(static b => b.Id).Select(static b => new { b.Name, Posts = b.Posts.Count, Liked = b.Posts.Count(static p => p.Likes > 10), Good = b.Rating > 2 }).ToListAsync(Ct))
            .ConvertAll(static row => $"{row.Name}|{row.Posts}|{row.Liked}|{row.Good}"));

        // Cambios y bajas.
        await pair.SameAsync(static async db =>
        {
            var blog = await db.Blogs.Include(static b => b.Posts).SingleAsync(static b => b.Id == 7, Ct);
            blog.Name = "renamed";
            blog.Notes = null;
            db.Remove(await db.Blogs.SingleAsync(static b => b.Id == 1, Ct));
            var posts = blog.Posts.OrderBy(static p => p.Id).ToList();
            posts[0].Likes += 100;
            db.Remove(posts[1]);
            db.Add(new Post { Id = new Guid(77, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1), Title = "added later", At = Epoch, Blog = blog, Author = await db.Authors.OrderBy(static a => a.Id).FirstAsync(Ct) });
            return [$"{await db.SaveChangesAsync(Ct)}", $"{await db.SaveChangesAsync(Ct)}"];
        });
        Assert.Equal(["5", "0"], pair.Last);
        await pair.SameAsync(static async db => Show(await db.Blogs.Include(static b => b.Posts).ThenInclude(static p => p.Author).OrderBy(static b => b.Id).ToListAsync(Ct)));

        // Una fila que otro borró: el cambio no afecta nada y EF lo dice.
        await pair.SameAsync(static async db =>
        {
            var blog = await db.Blogs.SingleAsync(static b => b.Id == 2, Ct);

            // Primero sus entradas: SQLite las borraría en cascada, y TekiumDB se niega a dejar una sin su blog.
            await db.Posts.Where(static p => p.BlogId == 2).ExecuteDeleteAsync(Ct);
            await db.Blogs.Where(static b => b.Id == 2).ExecuteDeleteAsync(Ct);
            blog.Rating = 99;
            var failure = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync(Ct));
            return [failure.GetType().Name, $"{await db.Blogs.CountAsync(Ct)}"];
        });

        // Una transacción que se deshace no deja nada.
        await pair.SameAsync(static async db =>
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                db.Add(new Blog { Name = "never", Rating = 1 });
                await db.SaveChangesAsync(Ct);
                await transaction.RollbackAsync(Ct);
            }

            return [$"{await db.Blogs.CountAsync(static b => b.Name == "never", Ct)}"];
        });
        Assert.Equal(["0"], pair.Last);
        await AssertNoLeaksAsync(pair.Database);
    }

    [Fact]
    public async Task More_of_linq_gives_what_sqlite_or_linq_to_objects_gives()
    {
        await using var pair = await Pair.CreateAsync(await OpenAsync(NewDevice()));
        await pair.SameAsync(static async db =>
        {
            await db.Database.EnsureCreatedAsync(Ct);
            var random = new Random(11);
            var authors = Enumerable.Range(0, 4).Select(static i => new Author { Name = $"writer {i}" }).ToList();
            for (var b = 0; b < 20; b++)
            {
                var blog = new Blog { Name = $"{(b % 2 == 0 ? "alpha" : "beta")} number {b}", Rating = random.Next(6), Notes = b % 4 == 0 ? null : $"n{b % 3}" };
                for (var p = 0; p < random.Next(4); p++)
                {
                    blog.Posts.Add(new Post
                    {
                        Id = new Guid(b, (short)p, 0, 0, 0, 0, 0, 0, 0, 0, 2), Title = $"title {b} {p}", Likes = random.Next(50),
                        Published = p % 2 == 0, At = Epoch.AddDays(b).AddHours(p), Author = authors[random.Next(4)],
                    });
                }

                db.Add(blog);
            }

            return [$"{await db.SaveChangesAsync(Ct)}"];
        });

        // Lo que SQLite también sabe hacer.
        await pair.SameAsync(static async db => Show(await db.Blogs.Where(static b => b.Name.StartsWith("alpha") && b.Name.EndsWith("8")).OrderBy(static b => b.Id).ToListAsync(Ct)));
        await pair.SameAsync(static async db => Show(await db.Blogs.Where(static b => b.Name.Contains("number 1")).OrderBy(static b => b.Id).ToListAsync(Ct)));
        await pair.SameAsync(static async db =>
        {
            var (start, part) = ("beta", "r 1");
            return Show(await db.Blogs.Where(b => b.Name.StartsWith(start) && b.Name.Contains(part)).OrderBy(static b => b.Id).ToListAsync(Ct));
        });
        await pair.SameAsync(static async db => Show(await db.Blogs.Where(static b => b.Name.ToUpper() == "ALPHA NUMBER 4" || b.Name.ToLower().Length == 13).OrderBy(static b => b.Id).ToListAsync(Ct)));
        await pair.SameAsync(static async db => await db.Blogs.OrderBy(static b => b.Id).Select(static b => b.Name + "!" + (b.Notes ?? "none")).ToListAsync(Ct));
        await pair.SameAsync(static async db => Show(await db.Blogs.Where(static b => (b.Rating * 2) + 1 > 6 && b.Rating % 2 == 1).OrderBy(static b => b.Id).ToListAsync(Ct)));
        await pair.SameAsync(static async db =>
            (await db.Posts.GroupBy(static p => p.BlogId).Select(static g => new { g.Key, Count = g.Count(), Likes = g.Sum(static p => p.Likes), Most = g.Max(static p => p.Likes) })
                .OrderBy(static g => g.Key).ToListAsync(Ct)).ConvertAll(static g => $"{g.Key}|{g.Count}|{g.Likes}|{g.Most}"));
        await pair.SameAsync(static async db =>
            (await db.Blogs.GroupBy(static b => b.Rating).Where(static g => g.Count() > 2).Select(static g => new { g.Key, Count = g.Count() }).OrderBy(static g => g.Key).ToListAsync(Ct))
            .ConvertAll(static g => $"{g.Key}|{g.Count}"));
        await pair.SameAsync(static async db =>
        [
            $"{await db.Blogs.Select(static b => b.Rating).Distinct().CountAsync(Ct)}",
            $"{await db.Blogs.AverageAsync(static b => b.Rating, Ct):F6}",
            $"{await db.Blogs.MinAsync(static b => b.Rating, Ct)}",
            $"{await db.Blogs.AllAsync(static b => b.Rating >= 0, Ct)}",
            $"{await db.Blogs.Select(static b => b.Id).ContainsAsync(3, Ct)}",
            $"{(await db.Blogs.OrderBy(static b => b.Id).LastAsync(Ct)).Name}",
            $"{await db.Blogs.Join(db.Posts, static b => b.Id, static p => p.BlogId, static (b, p) => b.Name + p.Title).CountAsync(Ct)}",
            $"{await db.Blogs.SelectMany(static b => b.Posts).CountAsync(static p => p.Published, Ct)}",
            $"{await db.Posts.Where(p => db.Blogs.Where(static b => b.Rating > 2).Select(static b => b.Id).Contains(p.BlogId)).CountAsync(Ct)}",
            $"{await db.Posts.CountAsync(static p => p.Blog!.Name.StartsWith("alpha") && p.Author!.Name == "writer 1", Ct)}",
            $"{await db.Blogs.FromSqlRaw("SELECT * FROM \"Blogs\"").CountAsync(static b => b.Rating > 1, Ct)}",
        ]);
        await pair.SameAsync(static async db =>
            (await db.Blogs.OrderBy(static b => b.Id).Select(static b => new { Kind = b.Rating > 3 ? "good" : b.Rating > 1 ? "fair" : "poor", Last = b.Posts.OrderByDescending(static p => p.Likes).Select(static p => p.Title).FirstOrDefault() })
                .ToListAsync(Ct)).ConvertAll(static row => $"{row.Kind}|{row.Last}"));
        await pair.SameAsync(static async db =>
        [
            $"{await db.Blogs.Where(static b => b.Rating < 2).ExecuteUpdateAsync(static set => set.SetProperty(static b => b.Rating, static b => b.Rating + 10).SetProperty(static b => b.Notes, "raised"), Ct)}",
            .. Show(await db.Blogs.Where(static b => b.Notes == "raised").OrderBy(static b => b.Id).ToListAsync(Ct)),
            $"{await db.Posts.Where(static p => p.Likes < 5).ExecuteDeleteAsync(Ct)}",
            $"{await db.Posts.CountAsync(Ct)}",
        ]);

        // Lo que SQLite no sabe (comparar y ordenar fechas con zona): contra LINQ sobre objetos.
        var options = new DbContextOptionsBuilder<Shop>().UseTekium(pair.Database).Options;
        await using var context = new Shop(options);
        var posts = await context.Posts.AsNoTracking().ToListAsync(Ct);
        var after = Epoch.AddDays(9).AddHours(1);
        Assert.Equal(
            posts.Where(p => p.At > after).OrderBy(static p => p.At).ThenBy(static p => p.Id).Select(static p => p.Id),
            await context.Posts.Where(p => p.At > after).OrderBy(static p => p.At).ThenBy(static p => p.Id).Select(static p => p.Id).ToListAsync(Ct));
        Assert.Equal(
            posts.Where(static p => p.At >= new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero) && p.Id != new Guid(12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2)).Select(static p => p.Id).Order(),
            (await context.Posts.Where(static p => p.At >= new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero) && p.Id != new Guid(12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2)).Select(static p => p.Id).ToListAsync(Ct)).Order());
        Assert.Equal(posts.Max(static p => p.At), await context.Posts.MaxAsync(static p => p.At, Ct));

        // Los métodos de texto comparan caracteres, como en .NET: las mayúsculas cuentan (en SQLite, LIKE no las distingue).
        Assert.Equal(0, await context.Blogs.CountAsync(static b => b.Name.StartsWith("ALPHA") || b.Name.EndsWith("NUMBER 8") || b.Name.Contains("Number"), Ct));
        Assert.Equal(10, await context.Blogs.CountAsync(static b => b.Name.StartsWith("alpha"), Ct));
        await AssertNoLeaksAsync(pair.Database);
    }

    [Fact]
    public async Task Every_supported_type_is_stored_read_back_and_compared()
    {
        await using var database = await OpenAsync(NewDevice());
        var options = new DbContextOptionsBuilder<Warehouse>().UseTekium(database).Options;
        var day = new DateOnly(2026, 11, 17);
        var rows = Enumerable.Range(1, 6).Select(i => new Crate
        {
            Id = i * 1_000_000_000_000L,
            Flag = i % 2 == 0,
            Tiny = (byte)(i * 40),
            Small = (short)(i * -3000),
            Whole = i * 100_000,
            Big = i * 9_000_000_000L,
            Single = i * 1.5f,
            Real = i * 2.25,
            Money = i * 1234.5678m,
            Text = $"crate '{i}'",
            Code = new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 9),
            Moment = new DateTime(2026, 1, i, 10, 30, 0, DateTimeKind.Utc),
            Stamp = new DateTimeOffset(2026, 2, i, 8, 0, 0, TimeSpan.FromHours(i - 3)),
            Day = day.AddDays(i),
            Hour = new TimeOnly(i, 15, 30),
            Span = TimeSpan.FromMinutes(i * 95),
            Bytes = [(byte)i, 0xFF, 0x00],
            Kind = (CrateKind)(i % 3),
            MaybeWhole = i % 2 == 0 ? null : i,
            MaybeText = i % 3 == 0 ? null : $"maybe {i}",
            MaybeStamp = i % 2 == 0 ? null : Epoch.AddDays(i),
            MaybeKind = i % 2 == 0 ? null : CrateKind.Fragile,
        }).ToList();
        await using (var db = new Warehouse(options))
        {
            await db.Database.EnsureCreatedAsync(Ct);
            db.AddRange(rows);
            Assert.Equal(6, await db.SaveChangesAsync(Ct));
        }

        await using var read = new Warehouse(options);
        var stored = await read.Crates.AsNoTracking().OrderBy(static c => c.Id).ToListAsync(Ct);
        Assert.Equal(rows.Select(Describe), stored.Select(Describe));

        // Cada tipo, comparado con un parámetro y con un valor escrito en la consulta.
        var third = rows[2];
        async Task SameAsync(System.Linq.Expressions.Expression<Func<Crate, bool>> filter)
        {
            var expected = rows.Where(filter.Compile()).Select(static c => c.Id).ToList();
            Assert.Equal(expected, await read.Crates.Where(filter).OrderBy(static c => c.Id).Select(static c => c.Id).ToListAsync(Ct));
        }

        await SameAsync(c => c.Flag == third.Flag);
        await SameAsync(static c => !c.Flag);
        await SameAsync(c => c.Tiny > third.Tiny);
        await SameAsync(c => c.Small < third.Small);
        await SameAsync(static c => c.Whole >= 300_000);
        await SameAsync(c => c.Big == third.Big);
        await SameAsync(static c => c.Big > 30_000_000_000L);
        await SameAsync(c => c.Real > third.Real);
        await SameAsync(static c => c.Real <= 6.75);
        await SameAsync(c => c.Money >= third.Money);
        await SameAsync(static c => c.Money < 3703.7034m);
        await SameAsync(c => c.Text == third.Text);
        await SameAsync(static c => c.Text == "crate '2'");
        await SameAsync(c => c.Code == third.Code);
        await SameAsync(static c => c.Code != new Guid(3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 9));
        await SameAsync(c => c.Moment > third.Moment);
        await SameAsync(static c => c.Moment <= new DateTime(2026, 1, 4, 10, 30, 0, DateTimeKind.Utc));
        await SameAsync(c => c.Stamp > third.Stamp);
        await SameAsync(static c => c.Stamp < new DateTimeOffset(2026, 2, 3, 8, 0, 0, TimeSpan.Zero));
        await SameAsync(c => c.Day > third.Day);
        await SameAsync(static c => c.Day == new DateOnly(2026, 11, 19));
        await SameAsync(c => c.Hour >= third.Hour);
        await SameAsync(static c => c.Hour < new TimeOnly(4, 0));
        await SameAsync(static c => c.Hour <= new TimeOnly(3, 15, 30));
        await SameAsync(static c => c.Hour < new TimeOnly(3, 15, 30));
        await SameAsync(c => c.Span > third.Span);
        await SameAsync(static c => c.Span == TimeSpan.FromMinutes(190));
        await SameAsync(c => c.Kind == third.Kind);
        await SameAsync(static c => c.Kind != CrateKind.Fragile);
        await SameAsync(static c => c.MaybeWhole == null);
        await SameAsync(static c => c.MaybeWhole > 2);
        await SameAsync(static c => c.MaybeText != null && c.MaybeText != "maybe 1");
        await SameAsync(static c => c.MaybeStamp != null);
        await SameAsync(static c => c.MaybeKind == CrateKind.Fragile);

        // Lo que EF escribió lo lee SQL directamente, con los mismos nombres y tipos.
        await using var connection = new TekiumDB.Sql.TkConnection(database);
        Assert.Equal(
            rows.Select(static c => $"{c.Id}|{c.Text}|{c.Whole}"),
            await Sql.SqlTests.QueryAsync(connection, "SELECT \"Id\", \"Text\", \"Whole\" FROM \"Crates\" ORDER BY \"Id\""));
        await AssertNoLeaksAsync(database);

        static string Describe(Crate c) => string.Create(
            CultureInfo.InvariantCulture,
            $"{c.Id}|{c.Flag}|{c.Tiny}|{c.Small}|{c.Whole}|{c.Big}|{c.Single:R}|{c.Real:R}|{c.Money}|{c.Text}|{c.Code}|{c.Moment:O}|{c.Stamp:O}|{c.Day:O}|{c.Hour:O}|{c.Span:c}|{Convert.ToHexString(c.Bytes)}|{c.Kind}|{c.MaybeWhole}|{c.MaybeText}|{c.MaybeStamp:O}|{c.MaybeKind}");
    }

    [Fact]
    public async Task Synchronous_calls_and_what_the_provider_does_not_have_say_so()
    {
        await using var database = await OpenAsync(NewDevice());
        var options = new DbContextOptionsBuilder<Shop>().UseTekium(database).Options;
        await using var db = new Shop(options);

        // TekiumDB no hace E/S síncrona: las versiones síncronas de EF lo dicen en vez de bloquear un hilo.
        Assert.Contains("Async", Assert.Throws<NotSupportedException>(() => db.Database.EnsureCreated()).Message, StringComparison.Ordinal);
        Assert.True(await db.Database.EnsureCreatedAsync(Ct));
        db.Add(new Blog { Name = "sync", Rating = 1 });
        Assert.Contains("Async", Assert.ThrowsAny<Exception>(() => db.SaveChanges()).GetBaseException().Message, StringComparison.Ordinal);
        Assert.Equal(1, await db.SaveChangesAsync(Ct));
        Assert.Contains("Async", Assert.Throws<NotSupportedException>(() => db.Blogs.ToList()).Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => db.Blogs.First());
        Assert.Throws<NotSupportedException>(() => db.Blogs.Count());
        Assert.Throws<NotSupportedException>(() => db.Database.EnsureDeleted());

        Assert.Contains("MigrateAsync", Assert.ThrowsAny<NotSupportedException>(() => db.Database.Migrate()).Message, StringComparison.Ordinal);

        // Lo que el SQL de TekiumDB no tiene falla diciendo qué, al ejecutar la consulta.
        var union = await Assert.ThrowsAsync<TekiumDB.Sql.TkSqlException>(
            () => db.Blogs.Where(static b => b.Rating > 2).Union(db.Blogs.Where(static b => b.Rating < 2)).ToListAsync(Ct));
        Assert.Contains("UNION is not supported", union.Message, StringComparison.Ordinal);

        // Lo que EF no sabe traducir para este proveedor lo dice EF.
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Blogs.Where(static b => b.Name.Trim() == "x").ToListAsync(Ct));

        // Una clave de varias columnas no se puede crear.
        var composite = new DbContextOptionsBuilder<Ledger>().UseTekium(database).Options;
        await using var ledger = new Ledger(composite);
        Assert.Contains("exactly one column", (await Assert.ThrowsAsync<NotSupportedException>(() => ledger.Database.EnsureCreatedAsync(Ct))).Message, StringComparison.Ordinal);

        // Las claves foráneas están en la base: una referencia a algo que no existe no se guarda.
        db.Add(new Post { Id = Guid.NewGuid(), Title = "orphan", At = Epoch, BlogId = 999, AuthorId = 999 });
        var orphan = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.StartsWith("FK_Posts_", Assert.IsType<TkConstraintException>(orphan.InnerException).Constraint, StringComparison.Ordinal);
        db.ChangeTracker.Clear();

        // Pero sólo impiden: la base no borra en cascada. EF borra lo que tiene cargado; lo que no, hace fallar el borrado.
        var parent = new Blog { Name = "parent", Rating = 1 };
        parent.Posts.Add(new Post { Id = Guid.NewGuid(), Title = "child", At = Epoch, Author = new Author { Name = "someone" } });
        db.Add(parent);
        Assert.Equal(3, await db.SaveChangesAsync(Ct));
        db.ChangeTracker.Clear();
        var kept = await Assert.ThrowsAsync<TkConstraintException>(() => db.Blogs.Where(static b => b.Name == "parent").ExecuteDeleteAsync(Ct));
        Assert.Equal("FK_Posts_Blogs_BlogId", kept.Constraint);
        db.Remove(await db.Blogs.SingleAsync(static b => b.Name == "parent", Ct));
        Assert.IsType<TkConstraintException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct))).InnerException);
        db.ChangeTracker.Clear();
        db.Remove(await db.Blogs.Include(static b => b.Posts).SingleAsync(static b => b.Name == "parent", Ct));
        Assert.Equal(2, await db.SaveChangesAsync(Ct));
        Assert.Equal(0, await db.Posts.CountAsync(Ct));

        // EnsureDeletedAsync quita las tablas del modelo, y se pueden volver a crear.
        Assert.True(await db.Database.EnsureDeletedAsync(Ct));
        Assert.True(await db.Database.EnsureCreatedAsync(Ct));
        Assert.Equal(0, await db.Blogs.CountAsync(Ct));
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task A_context_over_a_connection_string_keeps_its_data_in_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tekium-ef-{Guid.NewGuid():N}.tkdb");
        try
        {
            var options = new DbContextOptionsBuilder<Shop>().UseTekium($"Data Source={path};Password=ef core;Cache Size=8").Options;
            await using (var db = new Shop(options))
            {
                Assert.True(await db.Database.EnsureCreatedAsync(Ct));
                db.Add(new Blog { Name = "kept", Rating = 5, Posts = { new Post { Id = Guid.NewGuid(), Title = "first", At = Epoch, Author = new Author { Name = "someone" } } } });
                Assert.Equal(3, await db.SaveChangesAsync(Ct));
            }

            // Otro contexto, otra apertura del archivo.
            await using (var db = new Shop(options))
            {
                Assert.False(await db.Database.EnsureCreatedAsync(Ct));
                var blog = await db.Blogs.Include(static b => b.Posts).ThenInclude(static p => p.Author).SingleAsync(Ct);
                Assert.Equal(("kept", 5, "first", "someone"), (blog.Name, blog.Rating, blog.Posts[0].Title, blog.Posts[0].Author!.Name));
            }

            // Y con una conexión que abre y cierra la aplicación.
            await using var connection = new TekiumDB.Sql.TkConnection($"Data Source={path};Password=ef core;Cache Size=8");
            await connection.OpenAsync(Ct);
            await using (var db = new Shop(new DbContextOptionsBuilder<Shop>().UseTekium(connection).Options))
            {
                Assert.Equal(1, await db.Blogs.CountAsync(Ct));
            }

            Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static List<string> Show(IEnumerable<Blog> blogs) =>
        [.. blogs.Select(static b => $"{b.Id}|{b.Name}|{b.Rating}|{b.Notes}|[{string.Join(";", b.Posts.OrderBy(static p => p.Id).Select(Text))}]")];

    private static List<string> Show(IEnumerable<Post> posts) => [.. posts.Select(Text)];

    private static List<string> Show(IEnumerable<Author> authors) =>
        [.. authors.Select(static a => $"{a.Id}|{a.Name}|{a.Email}|[{string.Join(";", a.Posts.OrderBy(static p => p.Id).Select(Text))}]")];

    private static string Text(Post p) => string.Create(
        CultureInfo.InvariantCulture, $"{p.Id:N}|{p.Title}|{p.Likes}|{p.Published}|{p.Archived}|{p.At:O}|blog {p.BlogId} {p.Blog?.Name}|author {p.AuthorId} {p.Author?.Name}");

    /// <summary>El mismo modelo sobre las dos bases.</summary>
    private sealed class Pair : IAsyncDisposable
    {
        private readonly SqliteConnection _sqlite = new("Data Source=:memory:");
        private DbContextOptions<Shop> _reference = null!;
        private DbContextOptions<Shop> _tekium = null!;

        public TekiumDb Database { get; private init; } = null!;

        public List<string> Last { get; private set; } = [];

        public static async Task<Pair> CreateAsync(TekiumDb database)
        {
            var pair = new Pair { Database = database };
            await pair._sqlite.OpenAsync(Ct);
            pair._reference = new DbContextOptionsBuilder<Shop>().UseSqlite(pair._sqlite).Options;
            pair._tekium = new DbContextOptionsBuilder<Shop>().UseTekium(database).Options;
            return pair;
        }

        /// <summary>Hace lo mismo con un contexto nuevo sobre cada base y exige el mismo resultado.</summary>
        public async Task SameAsync(Func<Shop, Task<List<string>>> work)
        {
            List<string> expected;
            await using (var reference = new Shop(_reference))
            {
                expected = await work(reference);
            }

            await using var tekium = new Shop(_tekium);
            Last = await work(tekium);
            Assert.Equal(expected, Last);
        }

        public async ValueTask DisposeAsync()
        {
            await _sqlite.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}

public sealed class Blog
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Rating { get; set; }

    public string? Notes { get; set; }

    public List<Post> Posts { get; } = [];
}

public sealed class Post
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public int Likes { get; set; }

    public bool Published { get; set; }

    public bool Archived { get; set; }

    public DateTimeOffset At { get; set; }

    public int BlogId { get; set; }

    public Blog? Blog { get; set; }

    public int AuthorId { get; set; }

    public Author? Author { get; set; }
}

public sealed class Author
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public List<Post> Posts { get; } = [];
}

public sealed class Shop(DbContextOptions<Shop> options) : DbContext(options)
{
    public DbSet<Blog> Blogs => Set<Blog>();

    public DbSet<Post> Posts => Set<Post>();

    public DbSet<Author> Authors => Set<Author>();
}

public enum CrateKind
{
    Plain = 0,
    Fragile = 1,
    Heavy = 2,
}

public sealed class Crate
{
    [System.ComponentModel.DataAnnotations.Schema.DatabaseGenerated(System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.None)]
    public long Id { get; set; }

    public bool Flag { get; set; }

    public byte Tiny { get; set; }

    public short Small { get; set; }

    public int Whole { get; set; }

    public long Big { get; set; }

    public float Single { get; set; }

    public double Real { get; set; }

    public decimal Money { get; set; }

    public string Text { get; set; } = string.Empty;

    public Guid Code { get; set; }

    public DateTime Moment { get; set; }

    public DateTimeOffset Stamp { get; set; }

    public DateOnly Day { get; set; }

    public TimeOnly Hour { get; set; }

    public TimeSpan Span { get; set; }

    public byte[] Bytes { get; set; } = [];

    public CrateKind Kind { get; set; }

    public int? MaybeWhole { get; set; }

    public string? MaybeText { get; set; }

    public DateTimeOffset? MaybeStamp { get; set; }

    public CrateKind? MaybeKind { get; set; }
}

public sealed class Warehouse(DbContextOptions<Warehouse> options) : DbContext(options)
{
    public DbSet<Crate> Crates => Set<Crate>();
}

public sealed class Entry
{
    public int Book { get; set; }

    public int Line { get; set; }

    public decimal Amount { get; set; }
}

public sealed class Ledger(DbContextOptions<Ledger> options) : DbContext(options)
{
    public DbSet<Entry> Entries => Set<Entry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Entry>().HasKey(static e => new { e.Book, e.Line });
}

public sealed class EfCoreTests_File : EfCoreTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class EfCoreTests_Memory : EfCoreTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
