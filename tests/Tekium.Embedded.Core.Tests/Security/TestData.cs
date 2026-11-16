// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Tests.Security;

internal static class TestData
{
    /// <summary>La misma secuencia que usó el script que generó los valores de referencia.</summary>
    public static byte[] Bytes(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 7) + 3);
        }

        return data;
    }

    public static byte[] Repeat(byte value, int length)
    {
        var data = new byte[length];
        data.AsSpan().Fill(value);
        return data;
    }
}
