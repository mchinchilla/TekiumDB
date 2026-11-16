// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using System.Numerics;

namespace Tekium.Embedded.Storage;

/// <summary>
/// Size of a database page. Fixed when the file is created and stored in the header;
/// it cannot change afterwards without rebuilding the file.
/// </summary>
/// <remarks>
/// Only 4, 8, 16 and 32 KiB are valid. <c>default(PageSize)</c> is <see cref="Default"/> (8 KiB),
/// so there is no representable invalid value.
/// </remarks>
public readonly struct PageSize : IEquatable<PageSize>
{
    private const int DefaultShift = 13;
    private const int MinBytes = 4096;
    private const int MaxBytes = 32768;

    private static readonly PageSize[] SupportedSizes =
    [
        new(-1), new(0), new(1), new(2),
    ];

    // Stored relative to 8 KiB so that the zero-initialized struct is the default page size.
    private readonly sbyte _shiftFromDefault;

    private PageSize(sbyte shiftFromDefault) => _shiftFromDefault = shiftFromDefault;

    /// <summary>Gets the default page size: 8 KiB.</summary>
    public static PageSize Default => default;

    /// <summary>Gets every supported page size, in ascending order.</summary>
    public static ReadOnlySpan<PageSize> Supported => SupportedSizes;

    /// <summary>Gets the page size in bytes.</summary>
    public int Bytes => 1 << (DefaultShift + _shiftFromDefault);

    /// <summary>Indicates whether two page sizes are equal.</summary>
    public static bool operator ==(PageSize left, PageSize right) => left.Equals(right);

    /// <summary>Indicates whether two page sizes differ.</summary>
    public static bool operator !=(PageSize left, PageSize right) => !left.Equals(right);

    /// <summary>Creates a page size from a byte count.</summary>
    /// <param name="bytes">4096, 8192, 16384 or 32768.</param>
    /// <returns>The page size.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is not a supported size.</exception>
    public static PageSize FromBytes(int bytes) =>
        TryFromBytes(bytes, out var size)
            ? size
            : throw new ArgumentOutOfRangeException(
                nameof(bytes), bytes, "Page size must be a power of two between 4 KiB and 32 KiB.");

    /// <summary>Tries to create a page size from a byte count.</summary>
    /// <param name="bytes">Candidate size in bytes.</param>
    /// <param name="size">The page size, or <see cref="Default"/> when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="bytes"/> is a supported size.</returns>
    public static bool TryFromBytes(int bytes, out PageSize size)
    {
        if (bytes is >= MinBytes and <= MaxBytes && BitOperations.IsPow2(bytes))
        {
            size = new PageSize((sbyte)(BitOperations.Log2((uint)bytes) - DefaultShift));
            return true;
        }

        size = default;
        return false;
    }

    /// <inheritdoc />
    public bool Equals(PageSize other) => _shiftFromDefault == other._shiftFromDefault;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PageSize other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _shiftFromDefault;

    /// <inheritdoc />
    public override string ToString() => Bytes.ToString(CultureInfo.InvariantCulture);
}
