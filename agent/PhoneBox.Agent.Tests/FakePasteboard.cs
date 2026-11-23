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
    public string? ReadShareableText() => Text;
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
