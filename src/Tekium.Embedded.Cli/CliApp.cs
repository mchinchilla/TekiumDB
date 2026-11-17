// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tekium.Embedded.Cli;

/// <summary>
/// The <c>tkdb</c> command line. Every command works on a closed file; none changes the file it is given
/// except <c>passwd</c>. Exit codes: 0 done and nothing wrong, 1 the file has problems, 2 the command
/// could not run.
/// </summary>
internal static class CliApp
{
    public const int Ok = 0;
    public const int Problems = 1;
    public const int Failed = 2;

    private const string Usage = """
        tkdb - TekiumDB file tools

        Usage:
          tkdb info    <file>                    What the header says. Needs no password.
          tkdb check   <file> [password]         Reads every page and reports what is wrong, without opening the database.
          tkdb compact <file> -o <new file> [password] [new password]
                                                 Writes a copy without the space past changes left behind.
          tkdb repair  <file> -o <new file> [password] [new password]
                                                 Writes a copy with everything that can still be read.
          tkdb passwd  <file> [password] <new password>
                                                 Changes the password of an encrypted file, in place.

        Password of the file, if it is encrypted (never on the command line itself):
          --password-env <VARIABLE>     Taken from an environment variable.
          --password-file <path>        The first line of a file.
          --password-stdin              The first line of standard input.

        New password, for compact, repair and passwd:
          --new-password-env <VARIABLE>
          --new-password-file <path>
          --no-password                 compact and repair only: the copy is not encrypted.

        Exit code: 0 nothing wrong, 1 the file has problems, 2 the command could not run.
        """;

    public static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        int code;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            text.AppendLine(Usage);
            code = args.Length == 0 ? Failed : Ok;
        }
        else
        {
            try
            {
                var arguments = Arguments.Parse(args, input, environment);
                code = args[0] switch
                {
                    "info" => await InfoAsync(arguments, text, cancellationToken).ConfigureAwait(false),
                    "check" => await CheckAsync(arguments, text, cancellationToken).ConfigureAwait(false),
                    "compact" => await CopyAsync(arguments, text, salvage: false, cancellationToken).ConfigureAwait(false),
                    "repair" => await CopyAsync(arguments, text, salvage: true, cancellationToken).ConfigureAwait(false),
                    "passwd" => await ChangePasswordAsync(arguments, text, cancellationToken).ConfigureAwait(false),
                    _ => throw new ArgumentException($"Unknown command '{args[0]}'. Run 'tkdb --help'."),
                };
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or CryptographicException or UnauthorizedAccessException or InvalidOperationException)
            {
                await error.WriteLineAsync($"tkdb: {exception.Message}".AsMemory(), cancellationToken).ConfigureAwait(false);
                return Failed;
            }
        }

        await output.WriteAsync(text.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        return code;
    }

    private static async Task<int> InfoAsync(Arguments arguments, StringBuilder text, CancellationToken cancellationToken)
    {
        // Told no password, an inspection of an encrypted file stops at the header.
        var inspection = await TkInspector.InspectAsync(arguments.File, default, cancellationToken).ConfigureAwait(false);
        WriteHeader(inspection, arguments.File, text);
        return inspection.PageSize == 0 ? Problems : Ok;
    }

    private static async Task<int> CheckAsync(Arguments arguments, StringBuilder text, CancellationToken cancellationToken)
    {
        var inspection = await TkInspector.InspectAsync(arguments.File, arguments.Password, cancellationToken).ConfigureAwait(false);
        WriteHeader(inspection, arguments.File, text);
        if (inspection.PagesChecked)
        {
            text.AppendLine();
            text.AppendLine(Invariant($"Pages in use: {inspection.PagesInUse}; free: {inspection.PagesFree}"));
            foreach (var tree in inspection.Trees)
            {
                var damaged = tree.DamagedPages == 0 ? string.Empty : Invariant($", {tree.DamagedPages} DAMAGED");
                text.AppendLine(Invariant($"  {tree.Name}: {tree.Entries} entries in {tree.Pages} pages{damaged}"));
            }
        }

        WriteFindings(inspection.Findings, text);
        var verdict = inspection switch
        {
            { IsHealthy: false } => "The file has problems.",
            { PagesChecked: false } => "Nothing wrong in the header. The pages were not checked.",
            _ => "Nothing wrong was found.",
        };
        text.AppendLine();
        text.AppendLine(verdict);
        return inspection.IsHealthy ? Ok : Problems;
    }

    private static async Task<int> CopyAsync(Arguments arguments, StringBuilder text, bool salvage, CancellationToken cancellationToken)
    {
        var destination = arguments.Output ?? throw new ArgumentException("Say where to write the copy with -o <new file>.");
        var options = new TkCopyOptions
        {
            Password = arguments.Password,
            NewPassword = arguments.NoPassword ? ReadOnlyMemory<char>.Empty : arguments.NewPassword,
        };
        var report = salvage
            ? await TkMaintenance.RepairAsync(arguments.File, destination, options, cancellationToken).ConfigureAwait(false)
            : await TkMaintenance.CompactAsync(arguments.File, destination, options, cancellationToken).ConfigureAwait(false);

        text.AppendLine(Invariant($"{report.EntriesCopied} entries copied to {destination}"));
        text.AppendLine(Invariant($"{report.SourceBytes} bytes became {report.DestinationBytes}"));
        foreach (var dropped in report.Dropped)
        {
            text.AppendLine($"Left out: {dropped}");
        }

        WriteFindings(report.Findings, text);
        return report.Findings.Any(static finding => finding.Severity != TkSeverity.Info) ? Problems : Ok;
    }

    private static async Task<int> ChangePasswordAsync(Arguments arguments, StringBuilder text, CancellationToken cancellationToken)
    {
        if (arguments.NewPassword is not { IsEmpty: false } replacement)
        {
            throw new ArgumentException("Give the new password with --new-password-env or --new-password-file.");
        }

        var database = await TekiumDb.OpenAsync(new DbOptions { Path = arguments.File, Password = arguments.Password, CacheSizeMb = 8 }, cancellationToken)
            .ConfigureAwait(false);
        await using (database.ConfigureAwait(false))
        {
            await database.ChangePasswordAsync(replacement, cancellationToken).ConfigureAwait(false);
        }

        text.AppendLine("The password was changed.");
        return Ok;
    }

    private static void WriteHeader(TkInspection inspection, string file, StringBuilder text)
    {
        text.AppendLine(Invariant($"{file}: {inspection.FileLength} bytes"));
        if (inspection.PageSize == 0)
        {
            return;
        }

        text.AppendLine(Invariant($"Format version {inspection.FormatVersion}, pages of {inspection.PageSize} bytes, {(inspection.IsEncrypted ? "encrypted" : "not encrypted")}{(inspection.IsCompressed ? ", compressed" : string.Empty)}"));
        foreach (var slot in inspection.RootSlots)
        {
            var state = slot.IsValid
                ? Invariant($"transaction {slot.TransactionId}, {slot.PageCount} pages, root page {slot.RootPage}")
                : "does not pass its check";
            text.AppendLine(Invariant($"Root slot {slot.Index}{(slot.Index == inspection.CurrentSlot ? " (current)" : string.Empty)}: {state}"));
        }
    }

    private static void WriteFindings(IReadOnlyList<TkFinding> findings, StringBuilder text)
    {
        if (findings.Count > 0)
        {
            text.AppendLine();
        }

        foreach (var finding in findings)
        {
            text.AppendLine($"{finding.Severity.ToString().ToUpperInvariant()}: {finding.Message}");
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>What the command line says, with the passwords already read from where it points.</summary>
    private sealed class Arguments
    {
        public string File { get; private set; } = string.Empty;

        public string? Output { get; private set; }

        public ReadOnlyMemory<char> Password { get; private set; }

        public ReadOnlyMemory<char>? NewPassword { get; private set; }

        public bool NoPassword { get; private set; }

        public static Arguments Parse(string[] args, TextReader input, Func<string, string?> environment)
        {
            var parsed = new Arguments();
            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-o" or "--output":
                        parsed.Output = Value(args, ref i);
                        break;
                    case "--password-env":
                        parsed.Password = FromEnvironment(Value(args, ref i), environment).AsMemory();
                        break;
                    case "--password-file":
                        parsed.Password = FirstLine(Value(args, ref i)).AsMemory();
                        break;
                    case "--password-stdin":
                        parsed.Password = (input.ReadLine() ?? throw new ArgumentException("Standard input is empty: no password was read.")).AsMemory();
                        break;
                    case "--new-password-env":
                        parsed.NewPassword = FromEnvironment(Value(args, ref i), environment).AsMemory();
                        break;
                    case "--new-password-file":
                        parsed.NewPassword = FirstLine(Value(args, ref i)).AsMemory();
                        break;
                    case "--no-password":
                        parsed.NoPassword = true;
                        break;
                    case var option when option.StartsWith('-'):
                        throw new ArgumentException($"Unknown option '{option}'. Run 'tkdb --help'.");
                    case var file when parsed.File.Length == 0:
                        parsed.File = file;
                        break;
                    default:
                        throw new ArgumentException($"Unexpected argument '{args[i]}'. Passwords are not given on the command line: see 'tkdb --help'.");
                }
            }

            if (parsed.File.Length == 0)
            {
                throw new ArgumentException("Say which file. Run 'tkdb --help'.");
            }

            if (parsed.NoPassword && parsed.NewPassword is not null)
            {
                throw new ArgumentException("--no-password and a new password contradict each other.");
            }

            return System.IO.File.Exists(parsed.File) ? parsed : throw new FileNotFoundException($"There is no file '{parsed.File}'.");
        }

        private static string Value(string[] args, ref int index) =>
            ++index < args.Length ? args[index] : throw new ArgumentException($"'{args[index - 1]}' needs a value.");

        private static string FromEnvironment(string variable, Func<string, string?> environment) =>
            environment(variable) is { Length: > 0 } value
                ? value
                : throw new ArgumentException($"The environment variable '{variable}' is not set or is empty.");

        private static string FirstLine(string path)
        {
            using var reader = new StreamReader(path);
            return reader.ReadLine() is { Length: > 0 } line ? line : throw new ArgumentException($"'{path}' has no password in its first line.");
        }
    }
}
