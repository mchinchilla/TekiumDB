// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Sql.Execution;

namespace Tekium.Embedded.Sql;

/// <summary>What an application sets up for SQL on a database.</summary>
public static class TkSql
{
    /// <summary>
    /// Gives SQL on this database the functions and procedures of the application: usually the
    /// <c>TkRoutines.All</c> the source generator writes from <see cref="TkFunctionAttribute"/> and
    /// <see cref="TkProcedureAttribute"/>. They live in the application, not in the file: every
    /// application that opens the database registers its own.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="routines">The functions and procedures.</param>
    /// <exception cref="ArgumentException">Another routine is already registered under one of the names.</exception>
    public static void UseRoutines(this TekiumDb database, IEnumerable<TkRoutine> routines)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(routines);
        SqlEngine.For(database).Register(routines);
    }
}
