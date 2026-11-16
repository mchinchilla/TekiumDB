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
