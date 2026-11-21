// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.CrashChild;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Transactions;

/// <summary>
/// Corte de energía simulado, más duro que matar el proceso: se pierde todo lo que no había pasado por un
/// flush, entero o a pedazos. Se corta antes de cada operación de escritura de la carga, una por una.
/// </summary>
public abstract class PowerLossTests : DeviceBackedTests
{
    private const int ImagesPerCrashPoint = 6;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, false)]
    [InlineData("power loss", false)]
    [InlineData(null, true)]
    [InlineData("power loss", true)]
    public async Task Losing_power_at_any_operation_leaves_the_last_acknowledged_state_or_the_one_in_flight(string? password, bool compress)
    {
        // Con compresión cada página es una escritura aparte: el triple de puntos donde cortar. Una carga
        // más corta recorre los mismos caminos sin triplicar lo que tarda la prueba.
        var workload = new CrashWorkload(seed: 11, transactions: compress ? 4 : 8, password, compress);
        var random = new Random(4);

        var complete = NewDevice();
        Assert.Equal(workload.Transactions, await RunUntilPowerLossAsync(workload, complete));
        var operations = complete.OperationCount;
        Assert.True(operations > 30, "The workload is too small to say anything about the commit.");

        for (var at = 1; at <= operations; at++)
        {
            var device = NewDevice();
            device.TrackUnflushedWrites = true;
            device.PowerLossAtOperation = at;
            var acknowledged = await RunUntilPowerLossAsync(workload, device);
            Assert.True(device.PowerLost);

            // Nada de lo pendiente llegó al disco; todo llegó; y varias mezclas al azar, sector por sector.
            await AssertImageRecoversAsync(static (_, _) => false, "nothing unflushed survived");
            await AssertImageRecoversAsync(static (_, _) => true, "everything unflushed survived");
            for (var image = 0; image < ImagesPerCrashPoint; image++)
            {
                var seed = random.Next();
                var wholeWrites = image % 2 == 0;
                await AssertImageRecoversAsync(
                    (write, sector) => HashCode.Combine(seed, write, wholeWrites ? 0 : sector) % 2 == 0,
                    $"random image {seed}, whole writes {wholeWrites}");
            }

            async Task AssertImageRecoversAsync(Func<int, long, bool> survives, string what)
            {
                var image = await device.PowerLossImageAsync(survives);
                try
                {
                    await CrashRecovery.AssertRecoversAsync(
                        workload, image, acknowledged, $"Power lost at operation {at} of {operations} ({what})", Ct);
                }
                finally
                {
                    await image.DestroyAsync();
                }
            }
        }
    }

    /// <summary>Devuelve cuántos commits se confirmaron: -1 si ni siquiera la creación.</summary>
    private static async Task<int> RunUntilPowerLossAsync(CrashWorkload workload, TestDevice device)
    {
        var acknowledged = -1;
        try
        {
            var pager = await workload.CreateAsync(device, Ct);
            await using (pager)
            {
                acknowledged = 0;
                await workload.RunAsync(pager, 0, count => acknowledged = count, Ct);
            }
        }
        catch (IOException) when (device.PowerLost)
        {
        }

        return acknowledged;
    }
}

public sealed class PowerLossTests_File : PowerLossTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class PowerLossTests_Memory : PowerLossTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
