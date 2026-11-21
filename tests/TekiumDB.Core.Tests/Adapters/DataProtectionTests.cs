// Copyright (c) Tekium. Licensed under the MIT License.

using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using TekiumDB.DataProtection;
using TekiumDB.Tests.Database;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Adapters;

/// <summary>
/// Las claves de Data Protection en una base. La interfaz que ASP.NET Core pide es síncrona: las claves se
/// sirven desde memoria y se escriben al archivo después, sin hacer esperar a quien las guarda.
/// </summary>
public abstract class DataProtectionTests : DatabaseTests
{
    [Fact]
    public async Task Stored_keys_are_served_from_memory_at_once_and_reach_the_file()
    {
        var device = NewDevice();
        await using (var database = await OpenAsync(device))
        {
            await using var repository = await TkXmlRepository.CreateAsync(database, Ct);
            Assert.Empty(repository.GetAllElements());

            repository.StoreElement(Key("one", "a"), "key-one");
            repository.StoreElement(Key("two", "b"), "key-two");
            repository.StoreElement(Key("anonymous", "c"), string.Empty);
            repository.StoreElement(Key("anonymous", "d"), null!);
            repository.StoreElement(Key("one", "replaced"), "key-one");

            // Guardar otra vez con el mismo nombre reemplaza; sin nombre, cada una es otra.
            Assert.Equal(["anonymous=c", "anonymous=d", "one=replaced", "two=b"], Show(repository.GetAllElements()));

            // Lo que se entrega es una copia: cambiarla no cambia lo guardado, ni cambiar después lo que se guardó.
            var mine = Key("mine", "original");
            repository.StoreElement(mine, "key-mine");
            mine.SetAttributeValue("value", "changed afterwards");
            repository.GetAllElements().First().SetAttributeValue("value", "changed by the reader");
            Assert.Equal(["anonymous=c", "anonymous=d", "mine=original", "one=replaced", "two=b"], Show(repository.GetAllElements()));

            // Leer no toca el archivo.
            await repository.FlushAsync(Ct);
            var reads = device.ReadCount;
            repository.GetAllElements();
            Assert.Equal(reads, device.ReadCount);
            Assert.Throws<ArgumentNullException>(() => repository.StoreElement(null!, "x"));
            await AssertNoLeaksAsync(database);
        }

        // Otra apertura las encuentra, tal como quedaron.
        await using var reopened = await OpenAsync(device);
        await using var again = await TkXmlRepository.CreateAsync(reopened, Ct);
        Assert.Equal(["anonymous=c", "anonymous=d", "mine=original", "one=replaced", "two=b"], Show(again.GetAllElements()));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await TkXmlRepository.CreateAsync(null!, Ct));
    }

    [Fact]
    public async Task Storing_does_not_wait_for_the_file_and_flushing_or_disposing_does()
    {
        var device = NewDevice();
        var database = await OpenAsync(device);
        await using (database)
        {
            var repository = await TkXmlRepository.CreateAsync(database, Ct);

            // Con el escritor de la base ocupado, guardar vuelve enseguida: la clave está en memoria, no en el archivo.
            await using (var busy = await database.BeginWriteAsync(Ct))
            {
                for (var i = 0; i < 40; i++)
                {
                    repository.StoreElement(Key($"k{i:00}", "first"), $"key-{i:00}");
                }

                repository.StoreElement(Key("k07", "second"), "key-07");
                Assert.Equal(40, repository.GetAllElements().Count);
                var flush = repository.FlushAsync(Ct);
                Assert.False(flush.IsCompleted);
                await using (var other = await TkXmlRepository.CreateAsync(database, Ct))
                {
                    Assert.Empty(other.GetAllElements());
                }

                // Dejar de esperar no detiene las escrituras.
                using var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.FlushAsync(impatient.Token));
            }

            // Liberado el escritor, llegan todas, en el orden en que se guardaron: la última gana.
            await repository.FlushAsync(Ct);
            await using (var other = await TkXmlRepository.CreateAsync(database, Ct))
            {
                var stored = Show(other.GetAllElements());
                Assert.Equal(40, stored.Count);
                Assert.Contains("k07=second", stored);
            }

            // Cerrar el repositorio espera a lo pendiente.
            repository.StoreElement(Key("late", "x"), "key-late");
            await repository.DisposeAsync();
            await using var after = await TkXmlRepository.CreateAsync(database, Ct);
            Assert.Contains("late=x", Show(after.GetAllElements()));
        }
    }

    [Fact]
    public async Task A_key_that_cannot_be_written_is_not_lost_in_silence()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        var repository = await TkXmlRepository.CreateAsync(database, Ct);
        repository.StoreElement(Key("good", "1"), "key-good");
        await repository.FlushAsync(Ct);

        // El disco falla al escribir la siguiente.
        device.FailOnWriteNumber = device.WriteCount + 1;
        repository.StoreElement(Key("unlucky", "2"), "key-unlucky");
        await Assert.ThrowsAsync<IOException>(() => repository.FlushAsync(Ct));
        device.FailOnWriteNumber = null;

        // En memoria sigue (Data Protection ya pudo haberla usado), pero quien pregunta se entera, y no se aceptan más.
        Assert.Equal(["good=1", "unlucky=2"], Show(repository.GetAllElements()));
        await Assert.ThrowsAsync<IOException>(() => repository.FlushAsync(Ct));
        var refused = Assert.Throws<InvalidOperationException>(() => repository.StoreElement(Key("next", "3"), "key-next"));
        Assert.IsType<IOException>(refused.InnerException);
        Assert.Equal(2, repository.GetAllElements().Count);

        // Cerrar no lanza; y el archivo tiene sólo la que se pudo escribir.
        await repository.DisposeAsync();
        await using var fresh = await TkXmlRepository.CreateAsync(database, Ct);
        Assert.Equal(["good=1"], Show(fresh.GetAllElements()));

        // Las que estaban en cola detrás de la que falló tampoco se escriben: el archivo no queda con una sí y otra no.
        var queued = await TkXmlRepository.CreateAsync(database, Ct);
        await using (var busy = await database.BeginWriteAsync(Ct))
        {
            queued.StoreElement(Key("first", "a"), "key-first");
            queued.StoreElement(Key("second", "b"), "key-second");
            device.FailOnWriteNumber = device.WriteCount + 1;
        }

        await Assert.ThrowsAsync<IOException>(() => queued.FlushAsync(Ct));
        device.FailOnWriteNumber = null;
        await queued.DisposeAsync();
        await using var after = await TkXmlRepository.CreateAsync(database, Ct);
        Assert.Equal(["good=1"], Show(after.GetAllElements()));
    }

    [Fact]
    public async Task What_data_protection_protects_can_be_unprotected_after_reopening_the_database()
    {
        var device = NewDevice();
        string secret;
        await using (var database = await OpenAsync(device, password: "keys at rest"))
        {
            await using var repository = await TkXmlRepository.CreateAsync(database, Ct);
            await using var provider = Services(repository);
            secret = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("tests").Protect("the launch code");
            Assert.NotEqual("the launch code", secret);
            Assert.Single(repository.GetAllElements());
            await repository.FlushAsync(Ct);
        }

        // Otro proceso, la misma base: la clave está ahí y lo protegido se lee.
        await using (var database = await OpenAsync(device, password: "keys at rest"))
        {
            await using var repository = await TkXmlRepository.CreateAsync(database, Ct);
            Assert.Single(repository.GetAllElements());
            await using var provider = Services(repository);
            Assert.Equal("the launch code", provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("tests").Unprotect(secret));
        }

        // Con una base vacía, la clave es otra y no se puede.
        await using var empty = await OpenAsync(NewDevice());
        await using var none = await TkXmlRepository.CreateAsync(empty, Ct);
        await using var stranger = Services(none);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => stranger.GetRequiredService<IDataProtectionProvider>().CreateProtector("tests").Unprotect(secret));

        static ServiceProvider Services(TkXmlRepository repository)
        {
            var services = new ServiceCollection();
            services.AddDataProtection().SetApplicationName("tekium-tests").PersistKeysToTekiumDb(repository);
            return services.BuildServiceProvider();
        }
    }

    private static XElement Key(string id, string value) => new("key", new XAttribute("id", id), new XAttribute("value", value));

    private static List<string> Show(IReadOnlyCollection<XElement> elements) =>
        [.. elements.Select(static element => $"{element.Attribute("id")!.Value}={element.Attribute("value")!.Value}").Order(StringComparer.Ordinal)];
}

public sealed class DataProtectionTests_File : DataProtectionTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class DataProtectionTests_Memory : DataProtectionTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
