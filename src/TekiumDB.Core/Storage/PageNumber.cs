// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB.Storage;

/// <summary>Position of a page inside the file. Page 0 is the header, so it doubles as "no page".</summary>
internal readonly record struct PageNumber(uint Value)
{
    public static PageNumber None => default;

    public bool IsNone => Value == 0;
}
