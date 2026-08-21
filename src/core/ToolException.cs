namespace CoreKeeperSkinTool;

/// <summary>
/// An error meant to be shown to the user.
/// Only the message is printed, not a stack trace, and the process exits with code 1.
///
/// The message itself is Japanese, which is right for the command line but not for the window:
/// that can be switched to English, and the one line saying what went wrong was the only part
/// of it that stayed Japanese. An error raised on a path the window can reach therefore also
/// carries a localization key and its arguments, and the window resolves those instead.
///
/// The written message stays either way. It is what the command line prints, it is the fallback
/// when no key was given, and it keeps the exception readable in a log or a debugger without
/// anything having to resolve it first.
/// </summary>
public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message)
    {
    }

    public ToolException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>
    /// Creates an error that the window can show in the language the user chose.
    /// </summary>
    /// <param name="messageKey">Key into the language files.</param>
    /// <param name="arguments">Values filled into the template, in order.</param>
    /// <param name="message">The Japanese text, used by the command line and as the fallback.</param>
    public ToolException(string messageKey, object?[] arguments, string message) : base(message)
    {
        MessageKey = messageKey;
        MessageArguments = arguments ?? [];
    }

    /// <summary>Key into the language files, or null when this error has no translation.</summary>
    public string? MessageKey { get; }

    /// <summary>Values to fill into the template named by <see cref="MessageKey"/>.</summary>
    public IReadOnlyList<object?> MessageArguments { get; } = [];
}
