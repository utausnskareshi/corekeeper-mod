using System.Globalization;

namespace CoreKeeperSkinTool.Cli;

/// <summary>
/// A parsed command line.
/// Accepts <c>--key value</c>, <c>--key=value</c>, <c>--flag</c> and the short form <c>-k value</c>.
/// An unknown option is an error rather than silently ignored, so typos are noticed.
/// </summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string> _options;
    private readonly HashSet<string> _flags;

    private CommandLine(string command, Dictionary<string, string> options, HashSet<string> flags)
    {
        Command = command;
        _options = options;
        _flags = flags;
    }

    /// <summary>Subcommand name; empty when none was given.</summary>
    public string Command { get; }

    /// <summary>
    /// Parses the arguments.
    /// </summary>
    /// <param name="args">The raw argument list.</param>
    /// <param name="aliases">Maps a short name to its full name, for example <c>i</c> to <c>input</c>.</param>
    /// <param name="valueOptions">Full names of options that take a value; anything else is treated as a flag.</param>
    /// <param name="knownFlags">Full names of options that take no value.</param>
    public static CommandLine Parse(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> aliases,
        IReadOnlySet<string> valueOptions,
        IReadOnlySet<string> knownFlags)
    {
        string command = string.Empty;
        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> flags = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];

            if (!arg.StartsWith('-'))
            {
                if (command.Length == 0)
                {
                    command = arg;
                    continue;
                }

                throw new ToolException($"解釈できない引数: {arg}");
            }

            // Counted rather than trimmed. TrimStart takes every leading dash, so "---width"
            // arrived as "width" and ran as though it had been spelled correctly - which is the
            // opposite of what this class promises about noticing typos.
            int dashes = arg.Length - arg.TrimStart('-').Length;
            if (dashes > 2)
            {
                throw new ToolException(
                    $"オプションの書き方が不正: {arg}{Environment.NewLine}" +
                    "  長い名前は -- 、短い名前は - で始めること。");
            }

            string token = arg[dashes..];
            string? inlineValue = null;
            int equals = token.IndexOf('=');
            if (equals >= 0)
            {
                inlineValue = token[(equals + 1)..];
                token = token[..equals];
            }

            if (token.Length == 0)
            {
                throw new ToolException($"オプション名が空: {arg}");
            }

            string name = aliases.TryGetValue(token, out string? resolved) ? resolved : token;

            if (knownFlags.Contains(name))
            {
                if (inlineValue is not null)
                {
                    throw new ToolException($"--{name} は値を取らないオプション。");
                }

                flags.Add(name);
                continue;
            }

            if (!valueOptions.Contains(name))
            {
                throw new ToolException($"未知のオプション: --{name}{Environment.NewLine}`cks help` で使い方を確認すること。");
            }

            string value;
            if (inlineValue is not null)
            {
                value = inlineValue;
            }
            else if (i + 1 < args.Count && !IsOptionToken(args[i + 1]))
            {
                value = args[++i];
            }
            else
            {
                throw new ToolException($"--{name} に値が無い。");
            }

            // Silently keeping the last occurrence hides a mistake: "--width 10 --width 20"
            // would run with 20 and never say that 10 was thrown away.
            if (options.TryGetValue(name, out string? existing))
            {
                throw new ToolException(
                    $"--{name} が複数回指定されている（{existing} と {value}）。どちらか一方にすること。");
            }

            options[name] = value;
        }

        return new CommandLine(command, options, flags);
    }

    /// <summary>A negative number such as -3 is treated as a value, not an option.</summary>
    private static bool IsOptionToken(string arg) =>
        arg.StartsWith('-') && !(arg.Length > 1 && (char.IsDigit(arg[1]) || arg[1] == '.'));

    public bool HasFlag(string name) => _flags.Contains(name);

    public bool HasOption(string name) => _options.ContainsKey(name);

    public string? GetString(string name) => _options.TryGetValue(name, out string? value) ? value : null;

    public string GetRequiredString(string name) =>
        GetString(name) ?? throw new ToolException($"--{name} は必須。`cks help` で使い方を確認すること。");

    public int GetInt(string name, int fallback)
    {
        string? raw = GetString(name);
        if (raw is null)
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new ToolException($"--{name} には整数を指定すること（指定値: {raw}）");
        }

        return value;
    }

    /// <summary>Reads a value and enforces the 0-255 range.</summary>
    public byte GetByte(string name, byte fallback)
    {
        int value = GetInt(name, fallback);
        if (value is < 0 or > 255)
        {
            throw new ToolException($"--{name} は 0〜255 の範囲で指定すること（指定値: {value}）");
        }

        return (byte)value;
    }

    /// <summary>Reads an enum value by name, ignoring case.</summary>
    public TEnum GetEnum<TEnum>(string name, TEnum fallback) where TEnum : struct, Enum
    {
        string? original = GetString(name);
        if (original is null)
        {
            return fallback;
        }

        // Trimmed first. Enum.TryParse ignores surrounding white space, so one leading space
        // was enough to get an ordinal past the check below: " 1" does not start with a digit
        // as written, and then parsed as the value numbered 1.
        string raw = original.Trim();

        // Enum.TryParse also accepts a comma-separated list and a bare ordinal, and Enum.IsDefined
        // only sees the combined result, so "static,bob" and "1" both slipped through as values
        // the help never mentions. Only a name is meant here, so both forms are refused up front.
        bool nameOnly = raw.Length > 0
                        && !raw.Contains(',')
                        && !char.IsAsciiDigit(raw[0])
                        && raw[0] is not ('+' or '-');

        if (nameOnly && Enum.TryParse(raw, ignoreCase: true, out TEnum value) && Enum.IsDefined(value))
        {
            return value;
        }

        string choices = string.Join(" / ", Enum.GetNames<TEnum>().Select(n => n.ToLowerInvariant()));
        throw new ToolException($"--{name} には次のいずれかを指定すること: {choices}（指定値: {original}）");
    }
}
