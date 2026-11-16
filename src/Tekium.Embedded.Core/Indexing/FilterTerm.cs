// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Indexing;

/// <summary>A condition on one field of a row: the encoded value of the field must be in the range.</summary>
/// <param name="Field">The field: a column, or past the columns, an expression.</param>
/// <param name="Range">The values accepted.</param>
internal readonly record struct FilterTerm(int Field, KeyRange Range);
