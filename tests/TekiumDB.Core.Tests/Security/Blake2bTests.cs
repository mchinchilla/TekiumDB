// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.Security;

namespace TekiumDB.Tests.Security;

/// <summary>
/// Valores esperados generados con <c>hashlib.blake2b</c> de Python (implementación de referencia),
/// sobre la entrada <c>byte i = (i * 7 + 3) mod 256</c>. Los dos primeros son además los del RFC 7693.
/// </summary>
public sealed class Blake2bTests
{
    [Fact]
    public void Matches_the_RFC_7693_vector_for_abc()
    {
        var hash = new byte[64];

        Blake2b.Hash("abc"u8, hash);

        Assert.Equal(
            "ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923",
            Convert.ToHexStringLower(hash));
    }

    [Theory]
    [InlineData(0, 64, "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce")]
    [InlineData(0, 32, "0e5751c026e543b2e8ab2eb06099daa1d1e5df47778f7787faab45cdf12fe3a8")]
    [InlineData(0, 1, "2e")]
    [InlineData(0, 20, "3345524abf6bbe1809449224b5972c41790b6cf2")]
    [InlineData(1, 64, "4fe4da61bcc756071b226843361d74944c72245d23e8245ea678c13fdcd7fe2ae529cf999ad99cc24f7a73416a18ba53e76c0afef83b16a568b12fbfc1a2674d")]
    [InlineData(3, 32, "9facd640130c81e7f318a8d5c660bc1b0f5d03861ffbd4cc1358e0e904593c28")]
    [InlineData(3, 1, "28")]
    [InlineData(63, 64, "70b2a0e6daecac22c7a2df82c06e3fc0b4c66bd5ef8098e4ed54e723b393d79ef3bceba079a01a14c6ef2ae2ed1171df1662cd14ef38e6f77b01c7f48144dd09")]
    [InlineData(64, 64, "3db7bb5c40745f0c975ac6bb8578f590e2cd2cc1fc6d13533ef725325c9fddff5cca24e7a591a0f6032a24fad0e09f6df873c4ff314628391f78df7f09cb7ed7")]
    [InlineData(65, 64, "149c114a3e8c6e06bafee27c9d0de0e39ef28294fa0d9f81876dcceb10bb41101e256593587e46b844819ed7ded90d56c0843df06c95d1695c3de635cd7a888e")]
    [InlineData(127, 64, "71546bbf9110ad184cc60f2eb120fcfd9b4dbbca7a7f1270045b8a23a6a4f4330f65c1f030dd2f5fabc6c57617242c37cf427bd90407fac5b9deffd3ae888c39")]
    [InlineData(128, 64, "2d9e329f42afa3601d646692b81c13e87fcaff5bf15972e9813d7373cb6d181f9599f4d513d4af4fd6ebd37497aceb29aba5ee23ed764d8510b552bd088814fb")]
    [InlineData(128, 32, "f0501d06597880592bc49234eef100ec1ff349058d0e9d9b753504e24af86dd6")]
    [InlineData(128, 20, "79ae684511c83fdee5f040c77bb7c9a317d3f2de")]
    [InlineData(129, 64, "47889df9eb4d717afc5019df5c6a83df00a0b8677395e078cd5778ace0f338a618e68b7d9afb065d9e6a01ccd31d109447e7fae771c3ee3e105709194122ba2b")]
    [InlineData(255, 64, "1a5199ac66a00e8a87ad1c7fbad30b33137dd8312bf6d98602dacf8f40ea2cb623a7fbc63e5a6bfa434d337ae7da5ca1a52502a215a3fe0297a151be85d88789")]
    [InlineData(256, 64, "91019c558584980249ca43eceed27e19f1c3c24161b93eed1eee2a6a774f60bf8a81b43750870bee1698feac9c5336ae4d5c842e7ead159bf3916387e8ded9ae")]
    [InlineData(1000, 64, "4bdd2c9cf31d797a81d245c989ffb7515143ca345c66f73087dd5c58bf642bf083ba16894eab79e3b08d5126404d833e7510271b50be36a7b7cbbb46f5c89fac")]
    [InlineData(1000, 32, "d62b6c768ce1afc8367e0498ab2f8e3f7c178c35b1429f14c4604b545d200f52")]
    [InlineData(1000, 20, "7347a87d12c5b902eefd08b8b5c14816494c2639")]
    public void Matches_the_reference_implementation(int inputLength, int outputLength, string expected)
    {
        var hash = new byte[outputLength];

        Blake2b.Hash(TestData.Bytes(inputLength), hash);

        Assert.Equal(expected, Convert.ToHexStringLower(hash));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(333)]
    public void Hashing_in_pieces_gives_the_same_result_as_hashing_at_once(int pieceSize)
    {
        var data = TestData.Bytes(1000);
        var hasher = new Blake2b(64);
        for (var offset = 0; offset < data.Length; offset += pieceSize)
        {
            hasher.Update(data.AsSpan(offset, Math.Min(pieceSize, data.Length - offset)));
        }

        var hash = new byte[64];
        hasher.Finish(hash);

        Assert.Equal(
            "4bdd2c9cf31d797a81d245c989ffb7515143ca345c66f73087dd5c58bf642bf083ba16894eab79e3b08d5126404d833e7510271b50be36a7b7cbbb46f5c89fac",
            Convert.ToHexStringLower(hash));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void An_output_size_outside_1_to_64_bytes_is_rejected(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Blake2b(size));
    }
}
