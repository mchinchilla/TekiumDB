// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.CrashChild;
using TekiumDB.Storage;
using TekiumDB.Tests.Indexing;

namespace TekiumDB.Tests.Transactions;

/// <summary>Lo que se le exige a una base después de un crash, venga de donde venga.</summary>
internal static class CrashRecovery
{
    /// <summary>
    /// La base abre en el último estado confirmado o en el de la transacción que estaba en vuelo, nunca
    /// en otro; su estructura está sana y no perdió páginas; y se puede seguir escribiendo en ella hasta
    /// terminar la carga.
    /// </summary>
    /// <param name="workload">La carga que se estaba ejecutando.</param>
    /// <param name="device">Lo que quedó en el almacenamiento.</param>
    /// <param name="acknowledged">Commits confirmados al llamador antes del crash; -1 si ni la creación lo fue.</param>
    /// <param name="context">Dónde ocurrió el crash, para el mensaje de error.</param>
    /// <param name="cancellationToken">Cancela la verificación.</param>
    public static async Task AssertRecoversAsync(
        CrashWorkload workload, IStorageDevice device, int acknowledged, string context, CancellationToken cancellationToken)
    {
        Pager pager;
        try
        {
            pager = await workload.OpenAsync(device, cancellationToken);
        }
        catch (InvalidDataException) when (acknowledged < 0)
        {
            // Murió mientras se creaba: nunca llegó a ser una base, y abrirla lo dice con claridad.
            await device.DisposeAsync();
            return;
        }

        int state;
        await using (pager)
        {
            state = await workload.MatchAsync(pager, cancellationToken);
            Assert.True(
                state >= 0 && (state == acknowledged || state == acknowledged + 1),
                $"{context}: {acknowledged} commits were acknowledged but the database opened in state {state}.");
            await BTreeVerifier.AssertNoLeaksAsync(pager, cancellationToken);

            await workload.RunAsync(pager, state, null, cancellationToken);
            Assert.Equal(workload.Transactions, await workload.MatchAsync(pager, cancellationToken));
            await BTreeVerifier.AssertNoLeaksAsync(pager, cancellationToken);
        }
    }
}
