// Copyright (c) Tekium. Licensed under the MIT License.

using System.Security.Cryptography;
using TekiumDB.Cli;
using TekiumDB.Security;
using TekiumDB.Tests.Database;

namespace TekiumDB.Tests.Maintenance;

/// <summary>La herramienta <c>tkdb</c>, llamada como desde una terminal: argumentos, salida y código de salida.</summary>
public sealed class CliTests : IDisposable
{
    private const int Page = 4096;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tekium-cli-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    public CliTests() => Directory.CreateDirectory(_directory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Help_is_shown_on_request_and_asking_for_nothing_is_an_error()
    {
        var (code, output, _) = await RunAsync("--help");
        Assert.Equal(CliApp.Ok, code);
        Assert.Contains("tkdb check", output, StringComparison.Ordinal);

        Assert.Equal(CliApp.Failed, (await RunAsync()).Code);
    }

    [Fact]
    public async Task Info_and_check_describe_a_healthy_file()
    {
        var file = await CreateAsync("healthy.tkdb");

        var (code, output, error) = await RunAsync("info", file);
        Assert.Equal((CliApp.Ok, string.Empty), (code, error));
        Assert.Contains("pages of 4096 bytes, not encrypted", output, StringComparison.Ordinal);
        Assert.Contains("(current): transaction 1", output, StringComparison.Ordinal);

        (code, output, error) = await RunAsync("check", file);
        Assert.Equal((CliApp.Ok, string.Empty), (code, error));
        Assert.Contains("table Counter: 500 entries", output, StringComparison.Ordinal);
        Assert.Contains("Nothing wrong was found.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_names_the_damaged_page_and_exits_with_one()
    {
        var file = await CreateAsync("damaged.tkdb");
        await DamageAsync(file, page: 3);

        var (code, output, _) = await RunAsync("check", file);

        Assert.Equal(CliApp.Problems, code);
        Assert.Contains("ERROR: Page 3 of table Counter cannot be read", output, StringComparison.Ordinal);
        Assert.Contains("DAMAGED", output, StringComparison.Ordinal);
        Assert.Contains("The file has problems.", output, StringComparison.Ordinal);

        // Y lo que no es una base se dice, con el mismo código.
        var noise = Path.Combine(_directory, "noise.bin");
        await File.WriteAllBytesAsync(noise, new byte[10_000], Ct);
        (code, output, _) = await RunAsync("check", noise);
        Assert.Equal(CliApp.Problems, code);
        Assert.Contains("not a TekiumDB database", output, StringComparison.Ordinal);
        Assert.Equal(CliApp.Problems, (await RunAsync("info", noise)).Code);
    }

    [Fact]
    public async Task An_encrypted_file_is_checked_with_a_password_that_never_goes_on_the_command_line()
    {
        var file = await CreateAsync("secret.tkdb", "open sesame");

        var (code, output, _) = await RunAsync("check", file);
        Assert.Equal(CliApp.Ok, code);
        Assert.Contains("The pages were not checked.", output, StringComparison.Ordinal);

        _environment["TKDB_PASSWORD"] = "open sesame";
        (code, output, _) = await RunAsync("check", file, "--password-env", "TKDB_PASSWORD");
        Assert.Equal(CliApp.Ok, code);
        Assert.Contains("table Counter: 500 entries", output, StringComparison.Ordinal);

        var passwordFile = Path.Combine(_directory, "password.txt");
        await File.WriteAllTextAsync(passwordFile, "open sesame\nignored\n", Ct);
        Assert.Equal(CliApp.Ok, (await RunAsync("check", file, "--password-file", passwordFile)).Code);
        Assert.Equal(CliApp.Ok, (await RunAsync(["check", file, "--password-stdin"], "open sesame\n")).Code);

        _environment["TKDB_PASSWORD"] = "wrong";
        (code, output, _) = await RunAsync("check", file, "--password-env", "TKDB_PASSWORD");
        Assert.Equal(CliApp.Problems, code);
        Assert.Contains("The password does not open the database", output, StringComparison.Ordinal);

        // Una contraseña escrita como argumento se rechaza: quedaría a la vista en la lista de procesos.
        var (rejected, _, error) = await RunAsync("check", file, "open sesame");
        Assert.Equal(CliApp.Failed, rejected);
        Assert.Contains("not given on the command line", error, StringComparison.Ordinal);
        Assert.Equal(CliApp.Failed, (await RunAsync("check", file, "--password-env", "NOT_SET")).Code);
    }

    [Fact]
    public async Task Compact_writes_a_new_file_and_repair_writes_what_can_be_read()
    {
        var file = await CreateAsync("source.tkdb");
        var compacted = Path.Combine(_directory, "compacted.tkdb");

        var (code, output, _) = await RunAsync("compact", file, "-o", compacted);
        Assert.Equal(CliApp.Ok, code);
        Assert.Contains("500 entries copied", output, StringComparison.Ordinal);
        Assert.Equal(CliApp.Ok, (await RunAsync("check", compacted)).Code);

        // Sin destino, o con uno que ya existe, no se hace nada.
        var (missing, _, error) = await RunAsync("compact", file);
        Assert.Equal(CliApp.Failed, missing);
        Assert.Contains("-o", error, StringComparison.Ordinal);
        Assert.Equal(CliApp.Failed, (await RunAsync("compact", file, "-o", compacted)).Code);

        await DamageAsync(file, page: 3);
        var refused = Path.Combine(_directory, "refused.tkdb");
        (code, _, error) = await RunAsync("compact", file, "-o", refused);
        Assert.Equal(CliApp.Failed, code);
        Assert.Contains("Repair it instead", error, StringComparison.Ordinal);
        Assert.False(File.Exists(refused));

        var repaired = Path.Combine(_directory, "repaired.tkdb");
        (code, output, _) = await RunAsync("repair", file, "-o", repaired);
        Assert.Equal(CliApp.Problems, code);
        Assert.Contains("ERROR: Page 3", output, StringComparison.Ordinal);
        Assert.Equal(CliApp.Ok, (await RunAsync("check", repaired)).Code);
    }

    [Fact]
    public async Task The_copy_and_the_file_itself_can_change_password()
    {
        var file = await CreateAsync("locked.tkdb", "first");
        _environment["OLD"] = "first";
        _environment["NEW"] = "second";

        // passwd: en el mismo archivo.
        Assert.Equal(CliApp.Failed, (await RunAsync("passwd", file, "--password-env", "OLD")).Code);
        var (code, output, _) = await RunAsync("passwd", file, "--password-env", "OLD", "--new-password-env", "NEW");
        Assert.Equal(CliApp.Ok, code);
        Assert.Contains("The password was changed.", output, StringComparison.Ordinal);
        await Assert.ThrowsAsync<CryptographicException>(async () => await OpenAsync(file, "first"));
        await using (await OpenAsync(file, "second"))
        {
        }

        // compact: la copia sin cifrar, o con otra contraseña.
        var plain = Path.Combine(_directory, "plain.tkdb");
        Assert.Equal(CliApp.Ok, (await RunAsync("compact", file, "-o", plain, "--password-env", "NEW", "--no-password")).Code);
        await using (var database = await OpenAsync(plain, null))
        {
            Assert.False(database.IsEncrypted);
        }

        var third = Path.Combine(_directory, "third.tkdb");
        var newPassword = Path.Combine(_directory, "new.txt");
        await File.WriteAllTextAsync(newPassword, "third", Ct);
        Assert.Equal(CliApp.Ok, (await RunAsync("compact", file, "-o", third, "--password-env", "NEW", "--new-password-file", newPassword)).Code);
        await using (await OpenAsync(third, "third"))
        {
        }

        Assert.Equal(CliApp.Failed, (await RunAsync("compact", file, "-o", "x.tkdb", "--password-env", "NEW", "--no-password", "--new-password-env", "NEW")).Code);
        var (unencrypted, _, error) = await RunAsync("passwd", plain, "--new-password-env", "NEW");
        Assert.Equal(CliApp.Failed, unencrypted);
        Assert.Contains("not encrypted", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_cannot_be_run_says_why_and_exits_with_two()
    {
        var file = await CreateAsync("any.tkdb");
        foreach (var args in new string[][] { ["frobnicate", file], ["check"], ["check", file, "--loud"], ["check", Path.Combine(_directory, "absent.tkdb")], ["compact", file, "-o"] })
        {
            var (code, output, error) = await RunAsync(args);
            Assert.Equal(CliApp.Failed, code);
            Assert.Empty(output);
            Assert.StartsWith("tkdb: ", error, StringComparison.Ordinal);
        }
    }

    private static ValueTask<TekiumDb> OpenAsync(string path, string? password) =>
        TekiumDb.OpenAsync(
            new DbOptions
            {
                Path = path,
                PageSize = Page,
                CacheSizeMb = 1,
                Password = password.AsMemory(),
                KeyDerivation = new Argon2Parameters(MemoryKiB: 64, Iterations: 1, Parallelism: 1),
            },
            Ct);

    private Task<(int Code, string Output, string Error)> RunAsync(params string[] args) => RunAsync(args, string.Empty);

    private async Task<(int Code, string Output, string Error)> RunAsync(string[] args, string input)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var reader = new StringReader(input);
        var code = await CliApp.RunAsync(args, reader, output, error, name => _environment.GetValueOrDefault(name), Ct);
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>Una base con una tabla de 500 filas, en un solo commit.</summary>
    private async Task<string> CreateAsync(string name, string? password = null)
    {
        var path = Path.Combine(_directory, name);
        await using var database = await OpenAsync(path, password);
        await using var writer = await database.BeginWriteAsync(Ct);
        for (var i = 0; i < 500; i++)
        {
            await database.Table<Counter>().InsertAsync(writer, new Counter { Id = i, Value = i }, Ct);
        }

        await writer.CommitAsync(Ct);
        return path;
    }

    private static async Task DamageAsync(string path, int page)
    {
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        bytes[(page * Page) + 200] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes, Ct);
    }
}
