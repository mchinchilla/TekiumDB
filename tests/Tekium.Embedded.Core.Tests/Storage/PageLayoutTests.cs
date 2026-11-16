// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Tests.Storage;

public sealed class PageLayoutTests
{
    [Fact]
    public void A_stamped_page_reports_its_identity()
    {
        var page = NewPage();

        Assert.Equal(PageType.BTreeLeaf, PageLayout.Type(page));
        Assert.Equal(new PageNumber(9), PageLayout.Number(page));
        Assert.Equal(new TransactionId(4), PageLayout.Transaction(page));
        Assert.Equal(4096 - 32, PageLayout.Payload(page).Length);
        Assert.Equal(4096 - 32, PageLayout.PayloadSize(PageSize.FromBytes(4096)));
    }

    [Fact]
    public void A_sealed_page_verifies()
    {
        var page = NewPage();
        PageLayout.Seal(page);

        PageLayout.Verify(page, new PageNumber(9));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(2000)]
    [InlineData(4079)]
    [InlineData(4080)]
    [InlineData(4087)]
    public void Any_flipped_bit_is_detected_and_the_page_is_named(int offset)
    {
        var page = NewPage();
        PageLayout.Seal(page);
        page[offset] ^= 0x10;

        var error = Assert.Throws<InvalidDataException>(() => PageLayout.Verify(page, new PageNumber(9)));

        Assert.Contains("9", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_valid_page_stored_at_the_wrong_position_is_detected()
    {
        var page = NewPage();
        PageLayout.Seal(page);

        Assert.Throws<InvalidDataException>(() => PageLayout.Verify(page, new PageNumber(10)));
    }

    [Fact]
    public void Restamping_changes_the_identity_but_not_the_type_or_payload()
    {
        var page = NewPage();

        PageLayout.Restamp(page, new PageNumber(77), new TransactionId(8));

        Assert.Equal(PageType.BTreeLeaf, PageLayout.Type(page));
        Assert.Equal(new PageNumber(77), PageLayout.Number(page));
        Assert.Equal(new TransactionId(8), PageLayout.Transaction(page));
        Assert.All(PageLayout.Payload(page).ToArray(), b => Assert.Equal(0x5A, b));
    }

    private static byte[] NewPage()
    {
        var page = new byte[4096];
        PageLayout.Stamp(page, PageType.BTreeLeaf, new PageNumber(9), new TransactionId(4));
        PageLayout.Payload(page).Span.Fill(0x5A);
        return page;
    }
}
