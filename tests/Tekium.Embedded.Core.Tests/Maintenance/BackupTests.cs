// Copyright (c) Tekium. Licensed under the MIT License.

using System.Security.Cryptography;
using Tekium.Embedded.Tests.Database;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Maintenance;

/// <summary>
/// Backup en caliente: una copia consistente de la base tal como estaba en un commit, hecha mientras se
/// sigue escribiendo. Criterio de la Fase 10.2: copia consistente con el escritor activo.
/// </summary>
public abstract class BackupTests : DatabaseTests
{
    private const int Rows = 400;

    [Fact]
    public async Task A_backup_holds_the_database_as_it_was_when_it_started_whatever_is_written_meanwhile()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        await WriteRoundAsync(database, 1);
        var before = database.LastTransactionId;
        var target = NewDevice();

        // Con el snapshot ya fijado y antes de copiar nada, el escritor reescribe todo varias veces y borra la mitad.
        var report = await database.BackupAsync(
            _ => target,
            null,
            async () =>
            {
                for (var round = 2; round <= 6; round++)
                {
                    await WriteRoundAsync(database, round);
                }

                await using var writer = await database.BeginWriteAsync(Ct);
                for (var i = 0; i < Rows; i += 2)
                {
                    await database.Table<Ticket>().DeleteAsync(writer, (long)i, Ct);
                }

                await writer.CommitAsync(Ct);
            },
            Ct);

        Assert.Equal(before, report.TransactionId);
        Assert.Equal(Rows * 6, report.EntriesCopied);
        await using (var copy = await OpenAsync(target))
        {
            var tickets = await ToListAsync(copy.Table<Ticket>().ScanAsync(Ct));
            Assert.Equal(Rows, tickets.Count);
            Assert.All(tickets, static ticket => Assert.Equal(1, ticket.Priority));
            Assert.Equal(
                tickets.Where(static t => t.Owner == "owner3").Select(static t => t.Id),
                (await ToListAsync(copy.Table<Ticket>().QueryAsync(Ticket.Fields.Owner.EqualTo("owner3"), Ct))).Select(static t => t.Id).Order());
            await AssertNoLeaksAsync(copy);
        }

        Assert.True((await TkInspector.InspectAsync(() => target, default, Ct)).IsHealthy);

        // La original siguió su camino, y lo que el backup retuvo vuelve a estar disponible.
        Assert.Equal(Rows / 2, (await ToListAsync(database.Table<Ticket>().ScanAsync(Ct))).Count);
        await AssertNoLeaksAsync(database);
        var size = database.Pager.Current.PageCount;
        await WriteRoundAsync(database, 7, from: 1, step: 2);
        await WriteRoundAsync(database, 8, from: 1, step: 2);
        Assert.Equal(size, database.Pager.Current.PageCount);
    }

    [Fact]
    public async Task While_a_writer_commits_without_pause_every_backup_is_one_whole_commit()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        await WriteRoundAsync(database, 0);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var commits = new Dictionary<ulong, int>();
        var writing = Task.Run(
            async () =>
            {
                for (var round = 1; !stop.IsCancellationRequested; round++)
                {
                    await WriteRoundAsync(database, round);
                    lock (commits)
                    {
                        commits[database.LastTransactionId] = round;
                    }
                }
            },
            Ct);

        try
        {
            // Backups uno tras otro hasta que el escritor haya confirmado unas cuantas veces en medio.
            for (var i = 0; i < 6 || Written() < 8; i++)
            {
                var target = NewDevice();
                var report = await database.BackupAsync(_ => target, null, null, Ct);
                await using var copy = await OpenAsync(target);
                var tickets = await ToListAsync(copy.Table<Ticket>().ScanAsync(Ct));

                // Todas las filas son de la misma ronda: la del commit que el backup dice haber copiado.
                Assert.Equal(Rows, tickets.Count);
                var round = Assert.Single(tickets.Select(static ticket => ticket.Priority).Distinct());
                lock (commits)
                {
                    if (commits.TryGetValue(report.TransactionId, out var expected))
                    {
                        Assert.Equal(expected, round);
                    }
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await writing;
        }

        await AssertNoLeaksAsync(database);

        int Written()
        {
            lock (commits)
            {
                return commits.Count;
            }
        }
    }

    [Fact]
    public async Task The_backup_of_an_encrypted_database_needs_a_password_of_its_own_or_an_explicit_no()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device, "source password");
        await WriteRoundAsync(database, 1);

        // Sin decir nada, no se escribe una copia sin cifrar de una base cifrada.
        await Assert.ThrowsAsync<ArgumentException>(async () => await database.BackupAsync(_ => NewDevice(), null, null, Ct));

        var locked = NewDevice();
        await database.BackupAsync(_ => locked, new TkBackupOptions { Password = "backup password".AsMemory() }, null, Ct);
        await Assert.ThrowsAsync<CryptographicException>(async () => await OpenAsync(locked, "source password"));
        await using (var copy = await OpenAsync(locked, "backup password"))
        {
            Assert.Equal(Rows, (await ToListAsync(copy.Table<Ticket>().ScanAsync(Ct))).Count);
        }

        var plain = NewDevice();
        await database.BackupAsync(_ => plain, new TkBackupOptions { Unencrypted = true }, null, Ct);
        await using (var copy = await OpenAsync(plain))
        {
            Assert.False(copy.IsEncrypted);
            Assert.Equal(Rows, (await ToListAsync(copy.Table<Ticket>().ScanAsync(Ct))).Count);
        }

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await database.BackupAsync(_ => NewDevice(), new TkBackupOptions { Password = "x".AsMemory(), Unencrypted = true }, null, Ct));
    }

    [Fact]
    public async Task The_backup_of_a_compressed_database_is_compressed()
    {
        var options = new DbOptions { InMemory = true, PageSize = 4096, CacheSizeMb = 1, Compression = true };
        var device = NewDevice();
        await using var database = await TekiumDb.OpenAsync(_ => device, options, Ct);
        await WriteRoundAsync(database, 1);
        var target = NewDevice();

        await database.BackupAsync(_ => target, null, null, Ct);

        await using var copy = await TekiumDb.OpenAsync(_ => target, options, Ct);
        Assert.True(copy.IsCompressed);
        Assert.Equal(Rows, (await ToListAsync(copy.Table<Ticket>().ScanAsync(Ct))).Count);
    }

    [Fact]
    public async Task A_backup_that_fails_or_is_cancelled_lets_go_of_what_it_held()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        await WriteRoundAsync(database, 1);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await database.BackupAsync(_ => NewDevice(), null, () => new ValueTask(cancel.CancelAsync()), cancel.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await database.BackupAsync(_ => NewDevice(), null, static () => throw new InvalidOperationException("stop"), Ct));

        // Nada quedó retenido: el archivo crece lo mismo que el de una base igual a la que nadie le pidió un backup.
        await using var control = await OpenAsync(NewDevice());
        await WriteRoundAsync(control, 1);
        for (var round = 2; round <= 6; round++)
        {
            await WriteRoundAsync(database, round);
            await WriteRoundAsync(control, round);
        }

        Assert.Equal(control.Pager.Current.PageCount, database.Pager.Current.PageCount);
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task By_path_the_backup_is_a_new_file_and_a_failed_one_is_removed()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tekium-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var database = await TekiumDb.OpenAsync(new DbOptions { Path = Path.Combine(directory, "live.tkdb"), PageSize = 4096, CacheSizeMb = 1 }, Ct);
            await WriteRoundAsync(database, 1);
            var backup = Path.Combine(directory, "backup.tkdb");

            var report = await database.BackupAsync(backup, cancellationToken: Ct);

            Assert.Equal(database.LastTransactionId, report.TransactionId);
            Assert.InRange(report.Bytes, 4096 * 3, new FileInfo(backup).Length);
            Assert.True((await TkInspector.InspectAsync(backup, cancellationToken: Ct)).IsHealthy);
            await Assert.ThrowsAsync<IOException>(async () => await database.BackupAsync(backup, cancellationToken: Ct));

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            var abandoned = Path.Combine(directory, "abandoned.tkdb");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await database.BackupAsync(abandoned, cancellationToken: cancelled.Token));
            Assert.False(File.Exists(abandoned));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Reading_for_a_backup_does_not_go_through_the_page_cache()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        // Más grande que la caché (256 páginas): recorrerla por la caché desalojaría lo que la aplicación usa.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 4000; i++)
            {
                await database.Table<Ticket>().InsertAsync(
                    writer, new Ticket { Id = i, Owner = $"owner{i % 17}", Title = $"Ticket {i}", Body = new string('b', 400) }, Ct);
            }

            await writer.CommitAsync(Ct);
        }

        Assert.True(database.Pager.Current.PageCount > 600);

        // Lo que la aplicación tiene caliente sigue caliente: después del backup, leerlo no toca el dispositivo.
        _ = await ToListAsync(database.Table<Ticket>().QueryAsync(Ticket.Fields.Id.AtMost(20), Ct));
        await database.BackupAsync(_ => NewDevice(), null, null, Ct);
        var reads = device.ReadCount;
        _ = await ToListAsync(database.Table<Ticket>().QueryAsync(Ticket.Fields.Id.AtMost(20), Ct));
        Assert.Equal(reads, device.ReadCount);
    }

    /// <summary>Escribe (o reescribe) las filas con la ronda en <c>Priority</c>, en un solo commit.</summary>
    private static async Task WriteRoundAsync(TekiumDb database, int round, int from = 0, int step = 1)
    {
        await using var writer = await database.BeginWriteAsync(Ct);
        for (var i = from; i < Rows; i += step)
        {
            await database.Table<Ticket>().UpsertAsync(
                writer, new Ticket { Id = i, Owner = $"owner{i % 17}", Priority = round, Title = $"Ticket {i}", Body = new string('b', 150) }, Ct);
        }

        await writer.CommitAsync(Ct);
    }
}

public sealed class BackupTests_File : BackupTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class BackupTests_Memory : BackupTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
