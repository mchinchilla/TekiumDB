// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Security;

/// <summary>Argon2 variant. The values are the type identifiers of RFC 9106 and enter the hash.</summary>
internal enum Argon2Type
{
    /// <summary>Data-dependent addressing: strongest against GPUs, exposed to side channels.</summary>
    Argon2d = 0,

    /// <summary>Data-independent addressing: side-channel resistant.</summary>
    Argon2i = 1,

    /// <summary>Independent for the first half of the first pass, dependent afterwards. The one TekiumDB uses.</summary>
    Argon2id = 2,
}
