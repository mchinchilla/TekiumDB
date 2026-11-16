// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Tests.Storage;

public sealed class PageSizeTests
{
    [Fact]
    public void Default_is_8_KiB()
    {
        Assert.Equal(8192, PageSize.Default.Bytes);
    }

    [Fact]
    public void Uninitialized_value_is_the_default_page_size()
    {
        // Un struct no puede impedir default(T); el estado por defecto debe ser válido.
        Assert.Equal(PageSize.Default, default(PageSize));
        Assert.Equal(8192, default(PageSize).Bytes);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(16384)]
    [InlineData(32768)]
    public void FromBytes_accepts_the_four_supported_sizes(int bytes)
    {
        var size = PageSize.FromBytes(bytes);

        Assert.Equal(bytes, size.Bytes);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-8192)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2048)]
    [InlineData(4095)]
    [InlineData(4097)]
    [InlineData(6000)]
    [InlineData(12288)]
    [InlineData(32767)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void FromBytes_rejects_anything_else(int bytes)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PageSize.FromBytes(bytes));

        Assert.Equal("bytes", ex.ParamName);
    }

    [Theory]
    [InlineData(4096, true)]
    [InlineData(32768, true)]
    [InlineData(0, false)]
    [InlineData(12288, false)]
    [InlineData(65536, false)]
    public void TryFromBytes_reports_validity_without_throwing(int bytes, bool expected)
    {
        var ok = PageSize.TryFromBytes(bytes, out var size);

        Assert.Equal(expected, ok);
        Assert.Equal(expected ? bytes : PageSize.Default.Bytes, size.Bytes);
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Assert.Equal(PageSize.FromBytes(16384), PageSize.FromBytes(16384));
        Assert.NotEqual(PageSize.FromBytes(4096), PageSize.FromBytes(16384));
        Assert.True(PageSize.FromBytes(4096) != PageSize.Default);
    }

    [Fact]
    public void Every_supported_size_fits_the_two_byte_header_field()
    {
        // La cabecera guarda el tamaño de página en 2 bytes (plan: "Formato de archivo").
        foreach (var size in PageSize.Supported)
        {
            Assert.InRange(size.Bytes, 1, ushort.MaxValue);
        }

        Assert.Equal([4096, 8192, 16384, 32768], PageSize.Supported.ToArray().Select(s => s.Bytes));
    }
}
