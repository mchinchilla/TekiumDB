// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded;

/// <summary>A row is larger than the engine can store.</summary>
public sealed class TkRowTooLargeException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TkRowTooLargeException"/> class.</summary>
    public TkRowTooLargeException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkRowTooLargeException"/> class.</summary>
    /// <param name="message">What happened.</param>
    public TkRowTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkRowTooLargeException"/> class.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">What caused it.</param>
    public TkRowTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The type of a row cannot read or write the table as it is stored.</summary>
public sealed class TkSchemaException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TkSchemaException"/> class.</summary>
    public TkSchemaException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkSchemaException"/> class.</summary>
    /// <param name="message">What happened.</param>
    public TkSchemaException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkSchemaException"/> class.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">What caused it.</param>
    public TkSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A row with the same key is already in the table.</summary>
public sealed class TkDuplicateKeyException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TkDuplicateKeyException"/> class.</summary>
    public TkDuplicateKeyException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkDuplicateKeyException"/> class.</summary>
    /// <param name="message">What happened.</param>
    public TkDuplicateKeyException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkDuplicateKeyException"/> class.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">What caused it.</param>
    public TkDuplicateKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A row cannot be written because the key one of its indexes would give it is longer than a key may be.
/// </summary>
public sealed class TkIndexKeyTooLargeException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TkIndexKeyTooLargeException"/> class.</summary>
    public TkIndexKeyTooLargeException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkIndexKeyTooLargeException"/> class.</summary>
    /// <param name="message">What was too large.</param>
    public TkIndexKeyTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkIndexKeyTooLargeException"/> class.</summary>
    /// <param name="message">What was too large.</param>
    /// <param name="innerException">The cause.</param>
    public TkIndexKeyTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A write would have broken a CHECK, UNIQUE or FOREIGN KEY constraint. The transaction it was part of
/// is aborted: it can only be rolled back.
/// </summary>
public sealed class TkConstraintException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TkConstraintException"/> class.</summary>
    public TkConstraintException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkConstraintException"/> class.</summary>
    /// <param name="message">What was violated.</param>
    public TkConstraintException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkConstraintException"/> class.</summary>
    /// <param name="message">What was violated.</param>
    /// <param name="innerException">The cause.</param>
    public TkConstraintException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal TkConstraintException(string message, string table, string constraint, string key)
        : base(message)
    {
        Table = table;
        Constraint = constraint;
        Key = key;
    }

    /// <summary>Gets the name of the table whose constraint was violated.</summary>
    public string? Table { get; }

    /// <summary>Gets the name of the constraint.</summary>
    public string? Constraint { get; }

    /// <summary>Gets the key of the row that violated it, as text.</summary>
    public string? Key { get; }
}

/// <summary>
/// The changes asked for are no longer retained: the consumer fell further behind than
/// <see cref="DbOptions.ChangeRetention"/> allows, or asked for changes from before the database kept them.
/// </summary>
public sealed class TkChangeFeedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TkChangeFeedException"/> class.</summary>
    public TkChangeFeedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkChangeFeedException"/> class.</summary>
    /// <param name="message">What is missing.</param>
    public TkChangeFeedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkChangeFeedException"/> class.</summary>
    /// <param name="message">What is missing.</param>
    /// <param name="innerException">The cause.</param>
    public TkChangeFeedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The state of the database as of a past transaction is no longer kept: it is older than the horizon
/// set by <see cref="DbOptions.ChangeRetention"/>, and its pages may have been reused.
/// </summary>
public sealed class TkStateUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TkStateUnavailableException"/> class.</summary>
    public TkStateUnavailableException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkStateUnavailableException"/> class.</summary>
    /// <param name="message">Which state, and why it is gone.</param>
    public TkStateUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkStateUnavailableException"/> class.</summary>
    /// <param name="message">Which state, and why it is gone.</param>
    /// <param name="innerException">The cause.</param>
    public TkStateUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
