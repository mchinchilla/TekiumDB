// Copyright (c) Tekium. Licensed under the MIT License.

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using TekiumDB.Caching;
using TekiumDB.Tests.Database;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Adapters;

/// <summary>
/// <c>IDistributedCache</c> sobre una base: lo que entra sale igual, vence cuando debe (con un reloj que
/// la prueba mueve) y lo vencido termina fuera del archivo.
/// </summary>
public abstract class CacheTests : DatabaseTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task What_is_set_is_what_is_read_until_it_is_removed_and_across_reopening()
    {
        var device = NewDevice();
        var big = new byte[200_000];
        Random.Shared.NextBytes(big);
        await using (var database = await OpenAsync(device))
        {
            var cache = new TkDistributedCache(database);
            Assert.Null(await cache.GetAsync("missing", Ct));
            await cache.SetAsync("a", [1, 2, 3], new DistributedCacheEntryOptions(), Ct);
            await cache.SetAsync("empty", [], new DistributedCacheEntryOptions(), Ct);
            await cache.SetAsync("big", big, new DistributedCacheEntryOptions(), Ct);
            Assert.Equal([1, 2, 3], await cache.GetAsync("a", Ct));
            Assert.Empty((await cache.GetAsync("empty", Ct))!);
            Assert.Equal(big, await cache.GetAsync("big", Ct));

            // Las claves distinguen mayúsculas, y poner otra vez reemplaza.
            Assert.Null(await cache.GetAsync("A", Ct));
            await cache.SetAsync("a", [9], new DistributedCacheEntryOptions(), Ct);
            Assert.Equal([9], await cache.GetAsync("a", Ct));

            await cache.RemoveAsync("empty", Ct);
            await cache.RemoveAsync("never there", Ct);
            Assert.Null(await cache.GetAsync("empty", Ct));
            await AssertNoLeaksAsync(database);
        }

        // Es una caché que sobrevive al reinicio.
        await using var reopened = await OpenAsync(device);
        var again = new TkDistributedCache(reopened);
        Assert.Equal([9], await again.GetAsync("a", Ct));
        Assert.Equal(big, await again.GetAsync("big", Ct));
        Assert.Null(await again.GetAsync("empty", Ct));
    }

    [Fact]
    public async Task Entries_expire_at_their_absolute_time_or_when_nobody_reads_them()
    {
        await using var database = await OpenAsync(NewDevice());
        var clock = new Clock();
        var cache = new TkDistributedCache(database, new TkCacheOptions { TimeProvider = clock, SlidingRenewalThreshold = 0 });

        await cache.SetAsync("at", [1], new DistributedCacheEntryOptions { AbsoluteExpiration = Start.AddMinutes(10) }, Ct);
        await cache.SetAsync("in", [2], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(20) }, Ct);
        await cache.SetAsync("sliding", [3], new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) }, Ct);
        await cache.SetAsync("idle", [4], new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) }, Ct);
        await cache.SetAsync("capped", [5], new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5), AbsoluteExpiration = Start.AddMinutes(12) }, Ct);
        await cache.SetAsync("refreshed", [6], new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) }, Ct);
        await cache.SetAsync("forever", [7], new DistributedCacheEntryOptions(), Ct);

        // Cada cuatro minutos se lee "sliding" y "capped", y se refresca "refreshed" sin leerla. "idle" no se toca.
        for (var minute = 4; minute <= 28; minute += 4)
        {
            clock.Now = Start.AddMinutes(minute);
            Assert.Equal(minute < 10 ? [1] : null, await cache.GetAsync("at", Ct));
            Assert.Equal(minute < 20 ? [2] : null, await cache.GetAsync("in", Ct));
            Assert.Equal([3], await cache.GetAsync("sliding", Ct));
            Assert.Equal(minute < 12 ? [5] : null, await cache.GetAsync("capped", Ct));
            await cache.RefreshAsync("refreshed", Ct);
            await cache.RefreshAsync("missing", Ct);
        }

        Assert.Null(await cache.GetAsync("idle", Ct));
        Assert.Equal([6], await cache.GetAsync("refreshed", Ct));
        clock.Now = Start.AddYears(50);
        Assert.Null(await cache.GetAsync("sliding", Ct));
        Assert.Equal([7], await cache.GetAsync("forever", Ct));

        // Una ventana más larga que lo que le queda de vida no la alarga, ni al ponerla ni al leerla; y leerla entonces no escribe.
        clock.Now = Start;
        await cache.SetAsync("short-lived", [9], new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30), AbsoluteExpiration = Start.AddMinutes(2) }, Ct);
        clock.Now = Start.AddMinutes(1);
        var before = database.LastTransactionId;
        Assert.Equal([9], await cache.GetAsync("short-lived", Ct));
        Assert.Equal(before, database.LastTransactionId);
        clock.Now = Start.AddMinutes(3);
        Assert.Null(await cache.GetAsync("short-lived", Ct));

        // Justo en el instante de vencer ya no está; un instante antes, sí.
        clock.Now = Start;
        await cache.SetAsync("edge", [8], new DistributedCacheEntryOptions { AbsoluteExpiration = Start.AddSeconds(1) }, Ct);
        clock.Now = Start.AddSeconds(1).AddTicks(-1);
        Assert.Equal([8], await cache.GetAsync("edge", Ct));
        clock.Now = Start.AddSeconds(1);
        Assert.Null(await cache.GetAsync("edge", Ct));

        // Lo que no tiene sentido se rechaza al poner.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.SetAsync("x", [1], new DistributedCacheEntryOptions { AbsoluteExpiration = clock.Now }, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => cache.SetAsync("x", null!, new DistributedCacheEntryOptions(), Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => cache.SetAsync(null!, [1], new DistributedCacheEntryOptions(), Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => cache.GetAsync(null!, Ct));
    }

    [Fact]
    public async Task Reading_a_sliding_entry_writes_only_after_part_of_its_window_has_passed()
    {
        await using var database = await OpenAsync(NewDevice());
        var clock = new Clock();
        var cache = new TkDistributedCache(database, new TkCacheOptions { TimeProvider = clock });
        await cache.SetAsync("s", [1], new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(10) }, Ct);
        await cache.SetAsync("fixed", [2], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) }, Ct);
        var written = database.LastTransactionId;

        // En la primera décima de la ventana, leer no escribe nada; tampoco leer una entrada que no desliza.
        clock.Now = Start.AddSeconds(59);
        Assert.Equal([1], await cache.GetAsync("s", Ct));
        await cache.RefreshAsync("s", Ct);
        Assert.Equal([2], await cache.GetAsync("fixed", Ct));
        Assert.Equal(written, database.LastTransactionId);

        // Pasada la décima, sí: y la entrada vive diez minutos desde esa lectura.
        clock.Now = Start.AddSeconds(60);
        Assert.Equal([1], await cache.GetAsync("s", Ct));
        Assert.Equal(written + 1, database.LastTransactionId);
        clock.Now = Start.AddSeconds(60).AddMinutes(10).AddTicks(-1);
        Assert.Equal([1], await cache.GetAsync("s", Ct));
        clock.Now = clock.Now.AddMinutes(10);
        Assert.Null(await cache.GetAsync("s", Ct));

        Assert.Throws<ArgumentOutOfRangeException>(() => new TkDistributedCache(database, new TkCacheOptions { SlidingRenewalThreshold = 1.5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TkDistributedCache(database, new TkCacheOptions { ExpiredItemsDeletionInterval = TimeSpan.FromSeconds(-1) }));
    }

    [Fact]
    public async Task Expired_entries_leave_the_file_during_a_later_write()
    {
        await using var database = await OpenAsync(NewDevice());
        var clock = new Clock();
        var cache = new TkDistributedCache(database, new TkCacheOptions { TimeProvider = clock, ExpiredItemsDeletionInterval = TimeSpan.FromMinutes(30) });
        for (var i = 0; i < 50; i++)
        {
            await cache.SetAsync($"short{i}", [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) }, Ct);
        }

        await cache.SetAsync("long", [2], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) }, Ct);

        // Vencidas, pero todavía no pasó el intervalo: siguen en el archivo. Pedirlo a mano las saca.
        clock.Now = Start.AddMinutes(10);
        await cache.SetAsync("other", [3], new DistributedCacheEntryOptions(), Ct);
        Assert.Equal(50, await cache.RemoveExpiredAsync(Ct));
        Assert.Equal(0, await cache.RemoveExpiredAsync(Ct));

        // Pasado el intervalo, la siguiente escritura las saca sola.
        for (var i = 0; i < 20; i++)
        {
            await cache.SetAsync($"again{i}", [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) }, Ct);
        }

        // El intervalo se cuenta desde la última vez que se sacaron, también si fue a mano: a los 25 minutos, nada.
        clock.Now = Start.AddMinutes(35);
        await cache.SetAsync("early", [4], new DistributedCacheEntryOptions(), Ct);
        Assert.Equal(20, await cache.RemoveExpiredAsync(Ct));
        for (var i = 0; i < 20; i++)
        {
            await cache.SetAsync($"more{i}", [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) }, Ct);
        }

        clock.Now = Start.AddMinutes(65);
        await cache.SetAsync("trigger", [4], new DistributedCacheEntryOptions(), Ct);
        Assert.Equal(0, await cache.RemoveExpiredAsync(Ct));
        Assert.Equal([2], await cache.GetAsync("long", Ct));
        Assert.Equal([4], await cache.GetAsync("trigger", Ct));

        // Con el intervalo en cero no se saca nada sola.
        var manual = new TkDistributedCache(database, new TkCacheOptions { TimeProvider = clock, ExpiredItemsDeletionInterval = TimeSpan.Zero });
        await manual.SetAsync("m", [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) }, Ct);
        clock.Now = Start.AddDays(2);
        await manual.SetAsync("n", [1], new DistributedCacheEntryOptions(), Ct);
        Assert.Equal(2, await manual.RemoveExpiredAsync(Ct));
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Random_operations_agree_with_a_model()
    {
        await using var database = await OpenAsync(NewDevice());
        var clock = new Clock();
        var cache = new TkDistributedCache(database, new TkCacheOptions { TimeProvider = clock, SlidingRenewalThreshold = 0, ExpiredItemsDeletionInterval = TimeSpan.FromMinutes(3) });
        var model = new Dictionary<string, (byte[] Value, DateTimeOffset Expires, DateTimeOffset Absolute, TimeSpan Sliding)>();
        var random = new Random(20261117);
        for (var step = 0; step < 1500; step++)
        {
            clock.Now = clock.Now.AddSeconds(random.Next(0, 40));
            var key = $"k{random.Next(25)}";
            var alive = model.TryGetValue(key, out var held) && held.Expires > clock.Now;
            switch (random.Next(10))
            {
                case < 4:
                    var value = new byte[random.Next(0, 300)];
                    random.NextBytes(value);
                    var absolute = random.Next(3) == 0 ? clock.Now.AddSeconds(random.Next(1, 600)) : DateTimeOffset.MaxValue;
                    var sliding = random.Next(3) == 0 ? TimeSpan.FromSeconds(random.Next(1, 200)) : TimeSpan.Zero;
                    await cache.SetAsync(
                        key,
                        value,
                        new DistributedCacheEntryOptions
                        {
                            AbsoluteExpiration = absolute == DateTimeOffset.MaxValue ? null : absolute,
                            SlidingExpiration = sliding == TimeSpan.Zero ? null : sliding,
                        },
                        Ct);
                    model[key] = (value, sliding == TimeSpan.Zero ? absolute : Min(absolute, clock.Now + sliding), absolute, sliding);
                    break;

                case < 8:
                    Assert.Equal(alive ? held.Value : null, await cache.GetAsync(key, Ct));
                    Touch();
                    break;

                case 8:
                    await cache.RefreshAsync(key, Ct);
                    Touch();
                    break;

                default:
                    await cache.RemoveAsync(key, Ct);
                    model.Remove(key);
                    break;
            }

            void Touch()
            {
                if (alive && held.Sliding > TimeSpan.Zero)
                {
                    model[key] = held with { Expires = Min(held.Absolute, clock.Now + held.Sliding) };
                }
            }
        }

        // Lo que el modelo da por vencido es exactamente lo que queda por sacar del archivo... o ya salió sola.
        Assert.InRange(await cache.RemoveExpiredAsync(Ct), 0, model.Count(entry => entry.Value.Expires <= clock.Now));
        foreach (var (key, held) in model)
        {
            Assert.Equal(held.Expires > clock.Now ? held.Value : null, await cache.GetAsync(key, Ct));
        }

        await AssertNoLeaksAsync(database);

        static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    }

    [Fact]
    public async Task The_synchronous_methods_refuse_and_the_cache_registers_with_the_services_of_an_application()
    {
        await using var database = await OpenAsync(NewDevice());
        var services = new ServiceCollection();
        services.AddTekiumDistributedCache(_ => database, options => options.SlidingRenewalThreshold = 0);
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();
        Assert.IsType<TkDistributedCache>(cache);
        Assert.Same(cache, provider.GetRequiredService<IDistributedCache>());

        // Lo configurado al registrar se aplica: con el umbral en cero, leer una entrada que desliza la renueva enseguida.
        await cache.SetAsync("s", [1], new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(1) }, Ct);
        var before = database.LastTransactionId;
        await Task.Delay(20, Ct);
        Assert.Equal([1], await cache.GetAsync("s", Ct));
        Assert.Equal(before + 1, database.LastTransactionId);

        // Los métodos de extensión de IDistributedCache funcionan sobre los asíncronos.
        await cache.SetStringAsync("greeting", "hola", Ct);
        Assert.Equal("hola", await cache.GetStringAsync("greeting", Ct));

        // TekiumDB no hace E/S síncrona: los síncronos lo dicen en vez de bloquear un hilo.
        Assert.Throws<NotSupportedException>(() => cache.Get("greeting"));
        Assert.Throws<NotSupportedException>(() => cache.Set("k", [1], new DistributedCacheEntryOptions()));
        Assert.Throws<NotSupportedException>(() => cache.Refresh("greeting"));
        Assert.Throws<NotSupportedException>(() => cache.Remove("greeting"));
        Assert.Throws<ArgumentNullException>(() => new TkDistributedCache(null!));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public sealed class CacheTests_File : CacheTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class CacheTests_Memory : CacheTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
