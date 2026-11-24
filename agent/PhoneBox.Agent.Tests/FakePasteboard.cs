using PhoneBox.Agent;

/// <summary>A pasteboard for tests of the clipboard bridge: nothing here touches a real clipboard.</summary>
internal sealed class FakePasteboard : ITextPasteboard
{
    public long Count;
    public string? Text;
    public int Writes;
    public bool Fail;
    public bool ThrowOnCount;
    public long ChangeCount => ThrowOnCount ? throw new InvalidOperationException("boom") : Count;
    /// <summary>Runs once the text has been read, to put a copy between the two reads of one poll.</summary>
    public Action? AfterRead;
    public string? ReadShareableText() { var text = Text; AfterRead?.Invoke(); return text; }
    public bool WriteText(string text)
    {
        if (Fail) return false;
        Writes++;
        Text = text;
        Count++;
        return true;
    }
    /// <summary>Somebody copies something on the Mac (null: an item the bridge must not read).</summary>
    public void Copy(string? text) { Text = text; Count++; }
}
