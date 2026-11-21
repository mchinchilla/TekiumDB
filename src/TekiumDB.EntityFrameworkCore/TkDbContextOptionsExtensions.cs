// Copyright (c) Tekium. Licensed under the MIT License.

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using TekiumDB.Sql;

namespace TekiumDB.EntityFrameworkCore;

/// <summary>Tells Entity Framework Core to keep the data of a context in a TekiumDB database.</summary>
/// <remarks>
/// Only the asynchronous methods of Entity Framework Core work (<c>ToListAsync</c>, <c>SaveChangesAsync</c>,
/// <c>EnsureCreatedAsync</c>…): TekiumDB does no synchronous I/O, and the synchronous ones throw
/// <see cref="NotSupportedException"/>.
/// </remarks>
public static class TkDbContextOptionsExtensions
{
    /// <summary>Uses the database a connection string names.</summary>
    /// <param name="optionsBuilder">The options of the context.</param>
    /// <param name="connectionString">A connection string, as <see cref="TkConnection"/> takes it: <c>Data Source=app.tkdb;Password=…</c>.</param>
    /// <param name="configure">Changes options of the provider.</param>
    /// <returns>The same builder.</returns>
    public static DbContextOptionsBuilder UseTekium(
        this DbContextOptionsBuilder optionsBuilder, string connectionString, Action<TkDbContextOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        return Apply(optionsBuilder, extension => extension.WithConnectionString(connectionString), configure);
    }

    /// <summary>Uses a connection the application created. The context does not close what it did not open.</summary>
    /// <param name="optionsBuilder">The options of the context.</param>
    /// <param name="connection">The connection: a <see cref="TkConnection"/>.</param>
    /// <param name="configure">Changes options of the provider.</param>
    /// <returns>The same builder.</returns>
    public static DbContextOptionsBuilder UseTekium(
        this DbContextOptionsBuilder optionsBuilder, DbConnection connection, Action<TkDbContextOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(connection);
        return Apply(optionsBuilder, extension => extension.WithConnection(connection), configure);
    }

    /// <summary>Uses a database the application already opened, which the context shares and never closes.</summary>
    /// <param name="optionsBuilder">The options of the context.</param>
    /// <param name="database">The database.</param>
    /// <param name="configure">Changes options of the provider.</param>
    /// <returns>The same builder.</returns>
    public static DbContextOptionsBuilder UseTekium(
        this DbContextOptionsBuilder optionsBuilder, TekiumDb database, Action<TkDbContextOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(database);
        // The connection lives as long as the options: the context uses it and never closes the database behind it.
        return Apply(optionsBuilder, extension => extension.WithConnection(Share(database)), configure);
    }

    /// <summary>Uses the database a connection string names.</summary>
    /// <typeparam name="TContext">The type of the context.</typeparam>
    /// <param name="optionsBuilder">The options of the context.</param>
    /// <param name="connectionString">A connection string, as <see cref="TkConnection"/> takes it.</param>
    /// <param name="configure">Changes options of the provider.</param>
    /// <returns>The same builder.</returns>
    public static DbContextOptionsBuilder<TContext> UseTekium<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder, string connectionString, Action<TkDbContextOptionsBuilder>? configure = null)
        where TContext : DbContext => (DbContextOptionsBuilder<TContext>)UseTekium((DbContextOptionsBuilder)optionsBuilder, connectionString, configure);

    /// <summary>Uses a connection the application created.</summary>
    /// <typeparam name="TContext">The type of the context.</typeparam>
    /// <param name="optionsBuilder">The options of the context.</param>
    /// <param name="connection">The connection: a <see cref="TkConnection"/>.</param>
    /// <param name="configure">Changes options of the provider.</param>
    /// <returns>The same builder.</returns>
    public static DbContextOptionsBuilder<TContext> UseTekium<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder, DbConnection connection, Action<TkDbContextOptionsBuilder>? configure = null)
        where TContext : DbContext => (DbContextOptionsBuilder<TContext>)UseTekium((DbContextOptionsBuilder)optionsBuilder, connection, configure);

    /// <summary>Uses a database the application already opened, which the context shares and never closes.</summary>
    /// <typeparam name="TContext">The type of the context.</typeparam>
    /// <param name="optionsBuilder">The options of the context.</param>
    /// <param name="database">The database.</param>
    /// <param name="configure">Changes options of the provider.</param>
    /// <returns>The same builder.</returns>
    public static DbContextOptionsBuilder<TContext> UseTekium<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder, TekiumDb database, Action<TkDbContextOptionsBuilder>? configure = null)
        where TContext : DbContext => (DbContextOptionsBuilder<TContext>)UseTekium((DbContextOptionsBuilder)optionsBuilder, database, configure);

    private static TkConnection Share(TekiumDb database) => new(database);

    private static DbContextOptionsBuilder Apply(
        DbContextOptionsBuilder optionsBuilder, Func<RelationalOptionsExtension, RelationalOptionsExtension> change, Action<TkDbContextOptionsBuilder>? configure)
    {
        var extension = optionsBuilder.Options.FindExtension<TkOptionsExtension>() ?? new TkOptionsExtension();
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension((TkOptionsExtension)change(extension));
        configure?.Invoke(new TkDbContextOptionsBuilder(optionsBuilder));
        return optionsBuilder;
    }
}

/// <summary>The options of the TekiumDB provider that go beyond those of any relational one.</summary>
/// <param name="optionsBuilder">The options of the context.</param>
public sealed class TkDbContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
    : RelationalDbContextOptionsBuilder<TkDbContextOptionsBuilder, TkOptionsExtension>(optionsBuilder);

/// <summary>What the options of a context remember about the TekiumDB provider.</summary>
public sealed class TkOptionsExtension : RelationalOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    /// <summary>Initializes a new instance of the <see cref="TkOptionsExtension"/> class.</summary>
    public TkOptionsExtension()
    {
    }

    private TkOptionsExtension(TkOptionsExtension copyFrom)
        : base(copyFrom)
    {
    }

    /// <inheritdoc/>
    public override DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <inheritdoc/>
    public override void ApplyServices(IServiceCollection services) => services.AddEntityFrameworkTekium();

    /// <inheritdoc/>
    protected override RelationalOptionsExtension Clone() => new TkOptionsExtension(this);

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : RelationalExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => true;

        public override string LogFragment => "using TekiumDB ";

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["Tekium"] = "1";
    }
}
