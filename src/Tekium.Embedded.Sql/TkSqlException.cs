// Copyright (c) Tekium. Licensed under the MIT License.

using System.Data.Common;

namespace Tekium.Embedded.Sql;

/// <summary>A SQL statement could not be understood or run: a syntax error, an unknown table or column, a value of the wrong type.</summary>
public sealed class TkSqlException : DbException
{
    /// <summary>Initializes a new instance of the <see cref="TkSqlException"/> class.</summary>
    public TkSqlException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkSqlException"/> class.</summary>
    /// <param name="message">What is wrong, and where in the statement.</param>
    public TkSqlException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkSqlException"/> class.</summary>
    /// <param name="message">What is wrong, and where in the statement.</param>
    /// <param name="innerException">The cause.</param>
    public TkSqlException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
