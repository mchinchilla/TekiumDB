// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Sql;

namespace Tekium.Embedded.Tests.Sql;

/// <summary>Funciones y procedimientos de prueba. El generador escribe con ellos <c>TkRoutines.All</c> en este ensamblado.</summary>
public static class TestRoutines
{
    private static int _countedCalls;
    private static int _noisyCalls;
    private static int _vectorCalls;

    public static int CountedCalls => Volatile.Read(ref _countedCalls);

    public static int NoisyCalls => Volatile.Read(ref _noisyCalls);

    public static int VectorCalls => Volatile.Read(ref _vectorCalls);

    public static void Reset()
    {
        Volatile.Write(ref _countedCalls, 0);
        Volatile.Write(ref _noisyCalls, 0);
        Volatile.Write(ref _vectorCalls, 0);
    }

    [TkFunction("slugify", Deterministic = true)]
    public static string Slugify(ReadOnlySpan<char> text)
    {
        Span<char> slug = stackalloc char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            slug[i] = char.IsLetterOrDigit(text[i]) ? char.ToLowerInvariant(text[i]) : '-';
        }

        return new string(slug);
    }

    [TkFunction("with_tax")]
    public static decimal WithTax(decimal amount, double rate) => Math.Round(amount * (1 + (decimal)rate), 2);

    [TkFunction("initial")]
    public static string? Initial(string? text) => string.IsNullOrEmpty(text) ? null : text[..1];

    [TkFunction("positive")]
    public static int? Positive(int number) => number > 0 ? number : null;

    [TkFunction("is_recent")]
    internal static bool IsRecent(DateTimeOffset at, DateTime since, bool strict) => strict ? at.UtcDateTime > since : at.UtcDateTime >= since;

    [TkFunction("same_id")]
    public static bool SameId(Guid left, Guid right) => left == right;

    [TkFunction("reversed")]
    public static byte[] Reversed(byte[] bytes) => [.. bytes.Reverse()];

    [TkFunction("big_sum")]
    public static long BigSum(long a, long? b) => a + (b ?? 1000);

    /// <summary>Determinista, y cuenta cuántas veces la llaman.</summary>
    [TkFunction("counted", Deterministic = true)]
    public static long Counted(long number)
    {
        Interlocked.Increment(ref _countedCalls);
        return number * 2;
    }

    /// <summary>No determinista: se la llama una vez por fila.</summary>
    [TkFunction("noisy")]
    public static long Noisy(long number)
    {
        Interlocked.Increment(ref _noisyCalls);
        return number + 1;
    }

    [TkFunction("fails")]
    public static int Fails(int number) => number > 5 ? throw new InvalidOperationException("too big") : number;

    /// <summary>Vectorizada: un bloque de filas por llamada.</summary>
    [TkFunction("abs_delta", Vectorized = true)]
    public static void AbsDelta(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> result)
    {
        Interlocked.Increment(ref _vectorCalls);
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Math.Abs(a[i] - b[i]);
        }
    }

    [TkFunction("scaled", Vectorized = true, Deterministic = true)]
    public static void Scaled(ReadOnlySpan<long> values, ReadOnlySpan<int> factors, Span<long> result)
    {
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = values[i] * factors[i];
        }
    }

    [TkFunction("halved", Vectorized = true)]
    public static void Halved(ReadOnlySpan<float> values, Span<float> result)
    {
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = values[i] / 2;
        }
    }

    /// <summary>Borra, con la API tipada y en la transacción del CALL, los ítems con poca cantidad.</summary>
    [TkProcedure("drop_small")]
    public static async ValueTask<int> DropSmallAsync(TkContext context, int below, CancellationToken cancellationToken)
    {
        var items = context.Database.Table<Item>();
        var small = new List<long>();
        await foreach (var item in items.ScanAsync(context.Transaction, cancellationToken))
        {
            if (item.Qty < below)
            {
                small.Add(item.Id);
            }
        }

        foreach (var id in small)
        {
            await items.DeleteAsync(context.Transaction, id, cancellationToken);
        }

        return small.Count;
    }

    [TkProcedure("rename_owner")]
    public static async Task RenameOwnerAsync(TkContext context, string from, string? to)
    {
        var items = context.Database.Table<Item>();
        var renamed = new List<Item>();
        await foreach (var item in items.QueryAsync(context.Transaction, Item.Fields.Owner.EqualTo(from)))
        {
            renamed.Add(item with { Owner = to ?? "nobody" });
        }

        foreach (var item in renamed)
        {
            await items.UpdateAsync(context.Transaction, item);
        }
    }

    [TkProcedure("can_cancel")]
    public static ValueTask<bool> CanCancelAsync(TkContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(context is not null && cancellationToken.CanBeCanceled);

    /// <summary>Llama, con SQL, a un procedimiento guardado en el archivo: para probar que el límite de profundidad cuenta también por aquí.</summary>
    [TkProcedure("relay")]
    public static async ValueTask RelayAsync(TkContext context, CancellationToken cancellationToken) =>
        await context.ExecuteAsync("CALL bounce()", null, cancellationToken);

    /// <summary>SQL dentro del procedimiento, en su misma transacción: ve lo que acaba de escribir.</summary>
    [TkProcedure("zero_owner")]
    public static async ValueTask<long> ZeroOwnerAsync(TkContext context, string owner, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?> { ["owner"] = owner };
        var changed = await context.ExecuteAsync("UPDATE items SET qty = 0 WHERE owner = @owner", values, cancellationToken);
        var rows = await context.QueryAsync("SELECT COUNT(*) FROM items WHERE owner = @owner AND qty = 0", values, cancellationToken);
        return (long)rows[0][0]! == changed ? changed : -1;
    }
}
