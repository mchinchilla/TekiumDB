// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Security;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Tests.Security;

public sealed class AesGcmPageProtectorTests
{
    private const int PageBytes = 4096;

    [Fact]
    public void A_protected_page_comes_back_identical()
    {
        using var protector = new AesGcmPageProtector(TestData.Bytes(32));
        var page = NewPage(number: 9, transaction: 4);
        var stored = new byte[PageBytes];

        protector.Protect(page, stored);
        protector.Unprotect(stored, new PageNumber(9));

        Assert.True(page.AsSpan(0, PageBytes - PageLayout.TrailerSize).SequenceEqual(stored.AsSpan(0, PageBytes - PageLayout.TrailerSize)));
    }

    [Fact]
    public void The_header_stays_readable_and_the_payload_does_not()
    {
        using var protector = new AesGcmPageProtector(TestData.Bytes(32));
        var page = NewPage(number: 9, transaction: 4);
        var stored = new byte[PageBytes];

        protector.Protect(page, stored);

        Assert.Equal(PageType.BTreeLeaf, PageLayout.Type(stored));
        Assert.Equal(new PageNumber(9), PageLayout.Number(stored));
        Assert.Equal(new TransactionId(4), PageLayout.Transaction(stored));
        Assert.Equal(-1, stored.AsSpan(PageLayout.HeaderSize).IndexOf("TEKIUM-PLAINTEXT"u8));
        Assert.InRange(stored.AsSpan(PageLayout.HeaderSize).ToArray().Distinct().Count(), 200, 256);
    }

    [Fact]
    public void The_same_page_written_by_another_transaction_has_unrelated_ciphertext()
    {
        using var protector = new AesGcmPageProtector(TestData.Bytes(32));
        var first = new byte[PageBytes];
        var second = new byte[PageBytes];

        protector.Protect(NewPage(number: 9, transaction: 4), first);
        protector.Protect(NewPage(number: 9, transaction: 5), second);

        var equalBytes = 0;
        for (var i = PageLayout.HeaderSize; i < PageBytes; i++)
        {
            equalBytes += first[i] == second[i] ? 1 : 0;
        }

        Assert.InRange(equalBytes, 0, 80);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(2000)]
    [InlineData(4079)]
    [InlineData(4080)]
    [InlineData(4095)]
    public void Any_flipped_bit_fails_authentication_and_names_the_page(int offset)
    {
        // Incluye el encabezado en claro (tipo, número, transacción): va autenticado aunque no cifrado.
        using var protector = new AesGcmPageProtector(TestData.Bytes(32));
        var stored = new byte[PageBytes];
        protector.Protect(NewPage(number: 9, transaction: 4), stored);
        stored[offset] ^= 0x04;

        var error = Assert.Throws<InvalidDataException>(() => protector.Unprotect(stored, new PageNumber(9)));

        Assert.Contains("9", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_moved_to_another_position_is_rejected()
    {
        using var protector = new AesGcmPageProtector(TestData.Bytes(32));
        var stored = new byte[PageBytes];
        protector.Protect(NewPage(number: 9, transaction: 4), stored);

        Assert.Throws<InvalidDataException>(() => protector.Unprotect(stored, new PageNumber(10)));
    }

    [Fact]
    public void A_different_key_cannot_read_the_page()
    {
        using var writer = new AesGcmPageProtector(TestData.Bytes(32));
        using var other = new AesGcmPageProtector(TestData.Repeat(7, 32));
        var stored = new byte[PageBytes];
        writer.Protect(NewPage(number: 9, transaction: 4), stored);

        Assert.Throws<InvalidDataException>(() => other.Unprotect(stored, new PageNumber(9)));
    }

    [Fact]
    public void Header_records_are_authenticated_with_a_key_not_a_public_hash()
    {
        using var protector = new AesGcmPageProtector(TestData.Bytes(32));
        using var other = new AesGcmPageProtector(TestData.Repeat(7, 32));
        var plain = ChecksumPageProtector.Instance;
        var data = TestData.Bytes(32);
        var mine = new byte[16];
        var theirs = new byte[16];
        var hashed = new byte[16];

        protector.Authenticate(data, mine);
        other.Authenticate(data, theirs);
        plain.Authenticate(data, hashed);

        Assert.NotEqual(mine, theirs);
        Assert.NotEqual(mine, hashed);
        Assert.True(protector.Encrypts);
        Assert.False(plain.Encrypts);
    }

    private static byte[] NewPage(uint number, ulong transaction)
    {
        var page = new byte[PageBytes];
        PageLayout.Stamp(page, PageType.BTreeLeaf, new PageNumber(number), new TransactionId(transaction));
        var payload = PageLayout.Payload(page).Span;
        for (var i = 0; i + 16 <= payload.Length; i += 16)
        {
            "TEKIUM-PLAINTEXT"u8.CopyTo(payload[i..]);
        }

        return page;
    }
}
