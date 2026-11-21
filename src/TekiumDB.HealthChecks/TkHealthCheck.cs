// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TekiumDB.HealthChecks;

/// <summary>How a <see cref="TkHealthCheck"/> checks the database.</summary>
public sealed class TkHealthCheckOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether every check reads the whole database from the file and
    /// verifies it (<see cref="TekiumDb.VerifyAsync(CancellationToken)"/>). It finds damage a quick check
    /// cannot, and its cost grows with the size of the file: meant for a check that runs now and then,
    /// not every few seconds. Off by default.
    /// </summary>
    public bool Verify { get; set; }

    /// <summary>Gets or sets how many findings of a verification are reported by name. The rest are only counted.</summary>
    public int MaxReportedFindings { get; set; } = 5;
}

/// <summary>
/// Reports the state of a database to the health checks of the application. By default it is a quick
/// check that does no I/O: the database is open and still accepts writes. With
/// <see cref="TkHealthCheckOptions.Verify"/> it also reads and verifies every page in use.
/// </summary>
/// <param name="database">The database.</param>
/// <param name="options">How to check it. Null for a quick check.</param>
public sealed class TkHealthCheck(TekiumDb database, TkHealthCheckOptions? options = null) : IHealthCheck
{
    private readonly TekiumDb _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly TkHealthCheckOptions _options = options ?? new TkHealthCheckOptions();

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var failure = context.Registration.FailureStatus;
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        try
        {
            if (!_database.IsOpen)
            {
                return new HealthCheckResult(failure, "The database is closed.", null, data);
            }

            var read = _database.BeginRead();
            await using (read.ConfigureAwait(false))
            {
                data["transaction"] = read.TransactionId;
                data["encrypted"] = _database.IsEncrypted;
            }

            if (!_database.AcceptsWrites)
            {
                return new HealthCheckResult(
                    failure, "The database accepts no more writes: a commit failed while it was being made durable. Reopen it.", null, data);
            }

            if (!_options.Verify)
            {
                return HealthCheckResult.Healthy("The database is open and accepts writes.", data);
            }

            var verification = await _database.VerifyAsync(cancellationToken).ConfigureAwait(false);
            var problems = verification.Findings.Where(static finding => finding.Severity != TkSeverity.Info).ToList();
            var errors = problems.Count(static finding => finding.Severity == TkSeverity.Error);
            data["errors"] = errors;
            data["warnings"] = problems.Count - errors;
            for (var i = 0; i < Math.Min(problems.Count, _options.MaxReportedFindings); i++)
            {
                data[string.Create(CultureInfo.InvariantCulture, $"finding{i + 1}")] = problems[i].Message;
            }

            if (errors > 0)
            {
                return new HealthCheckResult(failure, $"The database is damaged: {problems.First(static finding => finding.Severity == TkSeverity.Error).Message}", null, data);
            }

            return problems.Count > 0
                ? HealthCheckResult.Degraded($"The database has warnings: {problems[0].Message}", null, data)
                : HealthCheckResult.Healthy("The database is open, accepts writes, and every page in use verifies.", data);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException or InvalidDataException)
        {
            return new HealthCheckResult(failure, $"The database cannot be checked: {exception.Message}", exception, data);
        }
    }
}

/// <summary>Registers a <see cref="TkHealthCheck"/> with the health checks of an application.</summary>
public static class TkHealthChecksBuilderExtensions
{
    /// <summary>Adds a health check for a TekiumDB database.</summary>
    /// <param name="builder">The health checks of the application.</param>
    /// <param name="database">Gives the database to check.</param>
    /// <param name="name">The name of the check.</param>
    /// <param name="configure">Changes how it checks: for one, to verify the whole file.</param>
    /// <param name="failureStatus">What to report when the check fails. Unhealthy if not given.</param>
    /// <param name="tags">Tags to filter checks by.</param>
    /// <returns>The same builder.</returns>
    public static IHealthChecksBuilder AddTekiumDb(
        this IHealthChecksBuilder builder,
        Func<IServiceProvider, TekiumDb> database,
        string name = "tekiumdb",
        Action<TkHealthCheckOptions>? configure = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(database);
        var options = new TkHealthCheckOptions();
        configure?.Invoke(options);
        return builder.Add(new HealthCheckRegistration(name, services => new TkHealthCheck(database(services), options), failureStatus, tags));
    }
}
