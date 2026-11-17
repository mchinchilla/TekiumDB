// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Tekium.Embedded.DataProtection;

/// <summary>A key of Data Protection, as the table <c>tekium_data_protection_keys</c> keeps it.</summary>
[TkTable("tekium_data_protection_keys")]
internal sealed partial class StoredKey
{
    /// <summary>Gets or sets the name the key was stored under: storing again under it replaces the key.</summary>
    [TkKey]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the XML of the key.</summary>
    public string Xml { get; set; } = string.Empty;
}

/// <summary>
/// Keeps the keys of ASP.NET Core Data Protection in a TekiumDB database.
/// </summary>
/// <remarks>
/// <para>
/// Data Protection asks for and stores keys through synchronous methods, and TekiumDB does no
/// synchronous I/O. So the keys are read once, when the repository is created, and served from memory;
/// and a key that is stored is kept in memory at once and written to the file right after, without
/// making the caller wait.
/// </para>
/// <para>
/// That leaves a window, normally a few milliseconds: if the process dies after a key was stored and
/// before its write finished, the key is lost, and what was protected with it in that window cannot be
/// unprotected. <see cref="FlushAsync"/> waits for everything stored so far to be in the file, and
/// disposing the repository does too. A write that fails is not silent: <see cref="FlushAsync"/> throws
/// it, and so does the next <see cref="StoreElement"/>.
/// </para>
/// <para>
/// The keys are stored as Data Protection gives them. They are encrypted at rest if the database is
/// (opened with a password) or if Data Protection is told to encrypt them (<c>ProtectKeysWith…</c>).
/// </para>
/// </remarks>
public sealed class TkXmlRepository : IXmlRepository, IAsyncDisposable
{
    private readonly TekiumDb _database;
    private readonly Table<StoredKey> _keys;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, XElement> _elements = new(StringComparer.Ordinal);
    private Task _pending = Task.CompletedTask;
    private long _unnamed;

    private TkXmlRepository(TekiumDb database)
    {
        _database = database;
        _keys = database.Table<StoredKey>();
    }

    /// <summary>Creates the repository, reading the keys the database holds.</summary>
    /// <param name="database">The database that holds the keys, in a table of its own.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The repository, ready to give to Data Protection.</returns>
    public static async ValueTask<TkXmlRepository> CreateAsync(TekiumDb database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        var repository = new TkXmlRepository(database);
        await foreach (var key in repository._keys.ScanAsync(cancellationToken).ConfigureAwait(false))
        {
            repository._elements[key.Name] = XElement.Parse(key.Xml);
        }

        return repository;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_gate)
        {
            // Copies: the caller may change what it gets.
            return [.. _elements.Values.Select(static element => new XElement(element))];
        }
    }

    /// <inheritdoc/>
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        lock (_gate)
        {
            if (_pending.IsFaulted)
            {
                throw new InvalidOperationException(
                    "An earlier key could not be written to the database, so the file does not hold every key. See the inner exception.",
                    _pending.Exception.InnerException);
            }

            // Without a name each key is its own entry; with one, storing again replaces it, as a file would be.
            var name = string.IsNullOrEmpty(friendlyName)
                ? string.Create(CultureInfo.InvariantCulture, $"unnamed-{DateTimeOffset.UtcNow.UtcTicks:x16}-{_unnamed++:x8}")
                : friendlyName;
            var copy = new XElement(element);
            _elements[name] = copy;
            _pending = WriteAfterAsync(_pending, name, copy.ToString(SaveOptions.DisableFormatting));
        }
    }

    /// <summary>Waits until every key stored so far is in the file.</summary>
    /// <param name="cancellationToken">Stops waiting; the writes go on.</param>
    /// <returns>A task that completes when the keys are durable, or fails with what stopped a write.</returns>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Task pending;
        lock (_gate)
        {
            pending = _pending;
        }

        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits for the keys stored so far to be written. It does not close the database.</summary>
    /// <returns>A task that completes when the pending writes have ended, well or not.</returns>
    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (_gate)
        {
            pending = _pending;
        }

        await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// Writes a key once the ones before it are written: they reach the file in the order they were
    /// stored. If one fails, the ones after it are not written: the failure stays in the task for
    /// whoever asks whether the keys are durable.
    /// </summary>
    private async Task WriteAfterAsync(Task previous, string name, string xml)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var writer = await _database.BeginWriteAsync().ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            await _keys.UpsertAsync(writer, new StoredKey { Name = name, Xml = xml }).ConfigureAwait(false);
            await writer.CommitAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Tells Data Protection to keep its keys in a TekiumDB database.</summary>
public static class TkDataProtectionBuilderExtensions
{
    /// <summary>Keeps the keys in the database of a repository created with <see cref="TkXmlRepository.CreateAsync"/>.</summary>
    /// <param name="builder">The Data Protection of the application.</param>
    /// <param name="repository">The repository.</param>
    /// <returns>The same builder.</returns>
    public static IDataProtectionBuilder PersistKeysToTekiumDb(this IDataProtectionBuilder builder, TkXmlRepository repository)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(repository);
        builder.Services.Configure<KeyManagementOptions>(options => options.XmlRepository = repository);
        return builder;
    }
}
