// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using System.Text;

namespace Tekium.Embedded;

/// <summary>
/// A path into a JSON document: <c>$</c>, then names (<c>.name</c> or <c>."any name"</c>) and positions
/// (<c>[2]</c>, or <c>[#-1]</c> for the last). Its text is canonical: two paths that mean the same are
/// written the same, which is what an index on a path compares.
/// </summary>
public sealed class TkJsonPath : IEquatable<TkJsonPath>
{
    /// <summary>The longest path accepted, in characters.</summary>
    public const int MaxLength = 1024;

    private static readonly TkJsonPath RootPath = new([], "$");

    private TkJsonPath(Step[] steps, string text)
    {
        Steps = steps;
        Text = text;
    }

    /// <summary>Gets the document itself: <c>$</c>.</summary>
    public static TkJsonPath Root => RootPath;

    /// <summary>Gets the canonical text of the path.</summary>
    public string Text { get; }

    internal Step[] Steps { get; }

    /// <summary>Reads a path. Throws <see cref="FormatException"/>, saying where, if it is not one.</summary>
    /// <param name="text">The path, starting with <c>$</c>.</param>
    /// <returns>The path.</returns>
    public static TkJsonPath Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out var path, out var error) ? path : throw new FormatException($"'{text}' is not a JSON path: {error}.");
    }

    /// <summary>Reads a path, without throwing.</summary>
    /// <param name="text">The path, starting with <c>$</c>.</param>
    /// <param name="path">The path, if it is one.</param>
    /// <returns>Whether it is one.</returns>
    public static bool TryParse(string text, out TkJsonPath path) => TryParse(text, out path, out _);

    /// <inheritdoc />
    public bool Equals(TkJsonPath? other) => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TkJsonPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    /// <inheritdoc />
    public override string ToString() => Text;

    private static bool TryParse(string text, out TkJsonPath path, out string error)
    {
        path = RootPath;
        error = string.Empty;
        if (text is null || text.Length == 0 || text.Length > MaxLength)
        {
            error = $"a path has from 1 to {MaxLength} characters";
            return false;
        }

        if (text[0] != '$')
        {
            error = "it must start with $";
            return false;
        }

        var steps = new List<Step>();
        var i = 1;
        while (i < text.Length)
        {
            if (text[i] == '.')
            {
                i++;
                if (i < text.Length && text[i] == '"')
                {
                    var name = new StringBuilder();
                    i++;
                    while (true)
                    {
                        if (i >= text.Length)
                        {
                            error = "a quoted name has no closing quote";
                            return false;
                        }

                        if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\')
                        {
                            name.Append(text[i + 1]);
                            i += 2;
                        }
                        else if (text[i] == '"')
                        {
                            i++;
                            break;
                        }
                        else
                        {
                            name.Append(text[i++]);
                        }
                    }

                    steps.Add(Step.Member(name.ToString()));
                }
                else
                {
                    var start = i;
                    while (i < text.Length && text[i] is not ('.' or '[' or '"' or ' '))
                    {
                        i++;
                    }

                    if (i == start)
                    {
                        error = $"a name is missing at position {start + 1}";
                        return false;
                    }

                    steps.Add(Step.Member(text[start..i]));
                }
            }
            else if (text[i] == '[')
            {
                var close = text.IndexOf(']', i);
                if (close < 0)
                {
                    error = $"the [ at position {i + 1} is not closed";
                    return false;
                }

                var inside = text[(i + 1)..close];
                var fromEnd = inside.StartsWith("#-", StringComparison.Ordinal);
                var digits = fromEnd ? inside[2..] : inside;
                if (digits.Length == 0 || digits.Length > 9 || !digits.All(char.IsAsciiDigit)
                    || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || (fromEnd && number == 0))
                {
                    error = $"[{inside}] is not a position: a number, or #- and a number from the end";
                    return false;
                }

                steps.Add(fromEnd ? Step.FromEnd(number) : Step.At(number));
                i = close + 1;
            }
            else
            {
                error = $"expected . or [ at position {i + 1}";
                return false;
            }
        }

        path = new TkJsonPath([.. steps], Canonical(steps));
        return true;
    }

    private static string Canonical(List<Step> steps)
    {
        var text = new StringBuilder("$");
        foreach (var step in steps)
        {
            if (step.Name is { } name)
            {
                if (name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                {
                    text.Append('.').Append(name);
                }
                else
                {
                    text.Append(".\"").Append(name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
                }
            }
            else
            {
                text.Append(step.IsFromEnd ? "[#-" : "[").Append(step.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
        }

        return text.ToString();
    }

    /// <summary>One step of a path: a member by name, or an element by position from the start or the end.</summary>
    internal readonly record struct Step(string? Name, int Index, bool IsFromEnd)
    {
        public static Step Member(string name) => new(name, 0, false);

        public static Step At(int index) => new(null, index, false);

        public static Step FromEnd(int count) => new(null, count, true);
    }
}
