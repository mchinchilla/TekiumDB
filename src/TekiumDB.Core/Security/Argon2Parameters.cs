// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB.Security;

/// <summary>Cost of an Argon2 derivation. Stored in the file header: the file knows how it was derived.</summary>
/// <param name="MemoryKiB">Memory in KiB; at least 8 per lane.</param>
/// <param name="Iterations">Passes over the memory.</param>
/// <param name="Parallelism">Lanes.</param>
internal readonly record struct Argon2Parameters(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>Gets the second recommended option of RFC 9106: 64 MiB, 3 passes, 4 lanes.</summary>
    public static Argon2Parameters Default => new(64 * 1024, 3, 4);
}
