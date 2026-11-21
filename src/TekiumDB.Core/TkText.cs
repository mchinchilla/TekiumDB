// Copyright (c) Tekium. Licensed under the MIT License.

using System.Text;

namespace TekiumDB;

/// <summary>
/// Text is stored as UTF-8, strictly: a string that is not valid Unicode is refused when written rather
/// than stored with its bad parts silently replaced, and damaged bytes are reported when read.
/// </summary>
internal static class TkText
{
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The length in UTF-8 bytes, or an <see cref="ArgumentException"/> if the text is not valid Unicode.</summary>
    public static int ByteCount(string value)
    {
        try
        {
            return Utf8.GetByteCount(value);
        }
        catch (EncoderFallbackException invalid)
        {
            throw new ArgumentException(
                "The text is not valid Unicode: it has half of a surrogate pair on its own.", nameof(value), invalid);
        }
    }
}
