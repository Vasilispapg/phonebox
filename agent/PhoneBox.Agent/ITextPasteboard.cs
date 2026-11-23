namespace PhoneBox.Agent;

/// <summary>The computer's clipboard as the phone bridge sees it: plain text only.</summary>
internal interface ITextPasteboard
{
    /// <summary>Goes up whenever anything is put on the clipboard, by any program, including this agent.</summary>
    long ChangeCount { get; }

    /// <summary>
    /// The plain text of the current item. Null when the clipboard is empty, holds no text, or the item is marked
    /// concealed, transient or auto-generated (password managers mark what they copy that way), because the bridge
    /// must not share those.
    /// </summary>
    string? ReadShareableText();

    /// <summary>Replaces the clipboard with this text. False when the system refused.</summary>
    bool WriteText(string text);
}
