// Copyright (c) Tekium. Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Tekium.Embedded.HealthChecks;
using Tekium.Embedded.Tests.Database;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Adapters;

/// <summary>El estado de la base, como lo ven los health checks de una aplicación.</summary>
public abstract class HealthCheckTests : DatabaseTests
{
    [Fact]
    public async Task A_healthy_database_reports_healthy_quickly_and_after_verifying()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        await FillAsync(database);

        // Rápido: no lee el archivo.
        var reads = device.ReadCount;
        var quick = await CheckAsync(new TkHealthCheck(database));
        Assert.Equal(HealthStatus.Healthy, quick.Status);
        Assert.Equal(database.LastTransactionId, quick.Data["transaction"]);
        Assert.Equal(false, quick.Data["encrypted"]);
        Assert.Equal(reads, device.ReadCount);

        // Algo que no es daño pero merece mirarse (aquí, una entrada del catálogo que esta versión no conoce) es "degradado".
        var odd = await OpenAsync(NewDevice());
        await using (odd)
        {
            await using (var writer = await odd.BeginWriteAsync(Ct))
            {
                await odd.Tree.PutAsync(writer.Inner, new byte[] { 200, 1 }, new byte[] { 1 }, Ct);
                await writer.CommitAsync(Ct);
            }

            var warned = await CheckAsync(new TkHealthCheck(odd, new TkHealthCheckOptions { Verify = true }));
            Assert.Equal(HealthStatus.Degraded, warned.Status);
            Assert.Equal((0, 1), ((int)warned.Data["errors"], (int)warned.Data["warnings"]));
            Assert.Equal(HealthStatus.Healthy, (await CheckAsync(new TkHealthCheck(odd))).Status);
        }

        // Verificando: lee cada página en uso.
        var verified = await CheckAsync(new TkHealthCheck(database, new TkHealthCheckOptions { Verify = true }));
        Assert.Equal(HealthStatus.Healthy, verified.Status);
        Assert.Equal(0, verified.Data["errors"]);
        Assert.Contains("verifies", verified.Description, StringComparison.Ordinal);
        Assert.True(device.ReadCount > reads);
    }

    [Fact]
    public async Task Damage_on_disk_is_found_only_by_the_check_that_verifies()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        await FillAsync(database);

        // Un byte cambiado en cada página después de las de cabecera: alguna está en uso.
        var size = Options.PageSize;
        for (var page = 3; page * (long)size < device.Length; page += 2)
        {
            await device.CorruptAsync((page * (long)size) + 100);
        }

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(new TkHealthCheck(database))).Status);
        var verified = await CheckAsync(new TkHealthCheck(database, new TkHealthCheckOptions { Verify = true, MaxReportedFindings = 1 }));
        Assert.Equal(HealthStatus.Unhealthy, verified.Status);
        Assert.Contains("damaged", verified.Description, StringComparison.Ordinal);
        Assert.True((int)verified.Data["errors"] + (int)verified.Data["warnings"] > 1, $"{verified.Data["errors"]} errors, {verified.Data["warnings"]} warnings");
        Assert.True(verified.Data.ContainsKey("finding1"));
        Assert.False(verified.Data.ContainsKey("finding2"));

        // El estado con que se registró el check es el que se informa al fallar.
        var degraded = await CheckAsync(new TkHealthCheck(database, new TkHealthCheckOptions { Verify = true }), HealthStatus.Degraded);
        Assert.Equal(HealthStatus.Degraded, degraded.Status);
    }

    [Fact]
    public async Task A_database_that_stopped_accepting_writes_or_was_closed_is_unhealthy()
    {
        var device = NewDevice();
        var database = await OpenAsync(device);
        await using (database)
        {
            Assert.True(database.AcceptsWrites);

            // Un commit cuyo resultado queda en duda: el flush que lo hace durable falla.
            for (var flush = 1; flush <= 4 && database.AcceptsWrites; flush++)
            {
                device.FailOnFlushNumber = device.FlushCount + flush;
                try
                {
                    await FillAsync(database);
                }
                catch (IOException)
                {
                }

                device.FailOnFlushNumber = null;
            }

            Assert.False(database.AcceptsWrites);
            var stopped = await CheckAsync(new TkHealthCheck(database));
            Assert.Equal(HealthStatus.Unhealthy, stopped.Status);
            Assert.Contains("accepts no more writes", stopped.Description, StringComparison.Ordinal);
        }

        // Cerrada, también; aunque estuviera sana.
        var healthy = await OpenAsync(NewDevice());
        Assert.True(healthy.IsOpen);
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(new TkHealthCheck(healthy))).Status);
        await healthy.DisposeAsync();
        Assert.False(healthy.IsOpen);
        foreach (var verify in new[] { false, true })
        {
            var closed = await CheckAsync(new TkHealthCheck(healthy, new TkHealthCheckOptions { Verify = verify }));
            Assert.Equal(HealthStatus.Unhealthy, closed.Status);
            Assert.Contains("closed", closed.Description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_check_registers_with_the_health_checks_of_an_application()
    {
        await using var database = await OpenAsync(NewDevice());
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddHealthChecks().AddTekiumDb(_ => database, configure: options => options.Verify = true, tags: ["storage"]);
        await using var provider = services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(Ct);
        Assert.Equal(HealthStatus.Healthy, report.Status);
        var entry = Assert.Single(report.Entries);
        Assert.Equal("tekiumdb", entry.Key);
        Assert.Equal(["storage"], entry.Value.Tags);
        Assert.Equal(0, entry.Value.Data["errors"]);
    }

    private static Task<HealthCheckResult> CheckAsync(TkHealthCheck check, HealthStatus failure = HealthStatus.Unhealthy) =>
        check.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("tekiumdb", check, failure, null) }, Ct);

    private static async Task FillAsync(TekiumDb database)
    {
        var chores = database.Table<Sql.Chore>();
        await using var writer = await database.BeginWriteAsync(Ct);
        for (var i = 0; i < 400; i++)
        {
            await chores.UpsertAsync(writer, new Sql.Chore { Id = i, Due = i % 7 }, Ct);
        }

        await writer.CommitAsync(Ct);
    }
}

public sealed class HealthCheckTests_File : HealthCheckTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class HealthCheckTests_Memory : HealthCheckTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
