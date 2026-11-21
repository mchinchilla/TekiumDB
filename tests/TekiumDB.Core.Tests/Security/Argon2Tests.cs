// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.Security;

namespace TekiumDB.Tests.Security;

public sealed class Argon2Tests
{
    /// <summary>
    /// Los tres vectores de la sección 5 del RFC 9106 (0 = Argon2d, 1 = Argon2i, 2 = Argon2id): 32 KiB, 3 pasadas, 4 carriles, con secreto y
    /// datos asociados. Criterio de salida de la Fase 2.
    /// </summary>
    [Theory]
    [InlineData(0, "512b391b6f1162975371d30919734294f868e3be3984f3c1a13a4db9fabe4acb")]
    [InlineData(1, "c814d9d1dc7f37aa13f0d77f2494bda1c8de6b016dd388d29952a4c4672b6ce8")]
    [InlineData(2, "0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659")]
    public void Matches_the_RFC_9106_test_vectors(int type, string expected)
    {
        var tag = new byte[32];

        Argon2.Derive(
            (Argon2Type)type,
            password: TestData.Repeat(0x01, 32),
            salt: TestData.Repeat(0x02, 16),
            secret: TestData.Repeat(0x03, 8),
            associatedData: TestData.Repeat(0x04, 12),
            new Argon2Parameters(MemoryKiB: 32, Iterations: 3, Parallelism: 4),
            tag);

        Assert.Equal(expected, Convert.ToHexStringLower(tag));
    }

    /// <summary>
    /// Valores generados con argon2-cffi 25.1.0, que envuelve la implementación de referencia en C.
    /// Contraseña <c>Bytes(n)</c> y sal <c>Bytes(n)</c> invertida, sin secreto ni datos asociados.
    /// Cubren etiquetas de más de 64 bytes, memoria no múltiplo de 4·p y uno a ocho carriles.
    /// </summary>
    [Theory]
    [InlineData(2, 32, 3, 4, 32, 16, "e7f6e6a6ba33ff8205eab926b1878d9891c6cff2e14721cd7a7edf46f8abf773")]
    [InlineData(2, 64, 1, 1, 8, 16, "b286b36f9906dd0820de08952da063c982afaf5421e36a3beaa8a385bae561b3")]
    [InlineData(2, 256, 2, 2, 11, 8, "af1aba36246e24d0a4877a1098c7e5eb5e1e42e6f9683d73939913dafe95bab462dc9bd271f6bd4b48993d2573f58e4d920cfcd79e7bc049651846f4b70bee3f")]
    [InlineData(2, 1024, 2, 8, 5, 16, "ce5467ef8653902d8f246c70752815bb")]
    [InlineData(2, 100, 3, 3, 40, 24, "81eef0174c0cd2d41fe2a7ad17f279e7aeaecc6ec0b6517f6760df8afa5b5dcfcf2e2481474c5c57b49ee1dbee45b450e4eb07f16782882e42265282a615b5e66eb91380084d76e8bf164f85584f9d365cda2b64bc57196d1f06fdc002b6a9db359023cd")]
    [InlineData(2, 4096, 1, 4, 1, 8, "451688e2f1e7d4a7a8ae68908e32878c44b8d506de15ce5f78681f1e95d05f5cce")]
    [InlineData(1, 64, 2, 1, 8, 16, "132caa6aa275eedf4362fff5fafe790c52c4bd9ed6247599a0204cda37435c74")]
    [InlineData(0, 64, 2, 1, 8, 16, "dc8df06a06bd7bb783b7b9a14ceefebf9729ffa833180d7ce2f7511992d9c308")]
    public void Matches_the_reference_implementation(
        int type, int memoryKiB, int iterations, int parallelism, int passwordLength, int saltLength, string expected)
    {
        var salt = TestData.Bytes(saltLength);
        Array.Reverse(salt);
        var tag = new byte[expected.Length / 2];

        Argon2.Derive(
            (Argon2Type)type, TestData.Bytes(passwordLength), salt, [], [], new Argon2Parameters(memoryKiB, iterations, parallelism), tag);

        Assert.Equal(expected, Convert.ToHexStringLower(tag));
    }

    [Fact]
    public void Matches_the_reference_command_line_example_at_64_MiB()
    {
        // echo -n "password" | argon2 somesalt -t 2 -m 16 -p 4 -l 24
        var tag = new byte[24];

        Argon2.Derive(Argon2Type.Argon2i, "password"u8, "somesalt"u8, [], [], new Argon2Parameters(65_536, 2, 4), tag);

        Assert.Equal("45d7ac72e76f242b20b77b9bf9bf9d5915894e669a24e6c6", Convert.ToHexStringLower(tag));
    }

    [Theory]
    [InlineData(7, 1, 1)]
    [InlineData(31, 1, 4)]
    [InlineData(64, 0, 1)]
    [InlineData(64, 1, 0)]
    public void Parameters_below_the_minimum_are_rejected(int memoryKiB, int iterations, int parallelism)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Argon2.Derive(
            Argon2Type.Argon2id, "password"u8, "somesalt"u8, [], [], new Argon2Parameters(memoryKiB, iterations, parallelism), new byte[32]));
    }

    [Fact]
    public void A_salt_shorter_than_8_bytes_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Argon2.Derive(
            Argon2Type.Argon2id, "password"u8, "short"u8, [], [], new Argon2Parameters(64, 1, 1), new byte[32]));
    }

    [Fact]
    public void Different_salts_give_unrelated_keys()
    {
        var first = new byte[32];
        var second = new byte[32];
        var parameters = new Argon2Parameters(64, 1, 1);

        Argon2.Derive(Argon2Type.Argon2id, "password"u8, "saltsalt"u8, [], [], parameters, first);
        Argon2.Derive(Argon2Type.Argon2id, "password"u8, "saltsalu"u8, [], [], parameters, second);

        Assert.NotEqual(first, second);
    }
}
