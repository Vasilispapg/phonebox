using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The clipboard bridge: what it accepts from the phone, what it publishes from the Mac, and the rules that keep one
/// copy from bouncing between the two. A fake pasteboard stands in for the Mac's, so no test touches a real clipboard.
/// </summary>
public sealed class ClipboardBridgeTests : IDisposable
{
    private sealed class FakePasteboard : ITextPasteboard
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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-bridge-clip-" + Guid.NewGuid());
    private readonly string _config;
    private readonly ConfigStore _store;
    private readonly FakePasteboard _pb = new();
    private readonly BridgeStream _stream = new();
    private readonly ClipboardBridge _bridge;
    private int _stamp;

    public ClipboardBridgeTests()
    {
        Directory.CreateDirectory(_root);
        _config = Path.Combine(_root, "config.json");
        _store = new ConfigStore(_config);
        SetSwitch(true);
        _bridge = new ClipboardBridge(_pb, _store, _stream, NullLogger<ClipboardBridge>.Instance);
    }

    /// <summary>Writes the config file with a new write time each time, which is how ConfigStore notices a change.</summary>
    private void SetSwitch(bool on)
    {
        File.WriteAllText(_config, "{\"bridge\":{\"clipboard\":" + (on ? "true" : "false") + "}}");
        File.SetLastWriteTimeUtc(_config, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_stamp));
    }

    private static string Of(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    // ---- from the phone ----

    [Fact]
    public void ReceiveWritesThePasteboardAndPublishesToOtherPhones()
    {
        Assert.Equal(ClipboardResult.Stored, _bridge.ReceiveFromPhone("hello", "phoneA"));
        Assert.Equal("hello", _pb.Text);
        var seen = Assert.Single(_stream.Since(0, "phoneB"));
        Assert.Equal(("clipboard", "hello", "phoneA"), (seen.Name, seen.Text, seen.Origin));
        Assert.Empty(_stream.Since(0, "phoneA"));
    }

    [Fact]
    public void OffSwitchRefusesAndWritesNothing()
    {
        SetSwitch(false);
        Assert.Equal(ClipboardResult.Off, _bridge.ReceiveFromPhone("hello", "phoneA"));
        Assert.Equal(0, _pb.Writes);
        Assert.Equal(0, _stream.Seq);
    }

    [Fact]
    public void AtTheLimitIsStoredOneByteOverIsRefused()
    {
        var atLimit = Of("a", ClipboardBridge.MaxBytes);
        Assert.Equal(ClipboardResult.Stored, _bridge.ReceiveFromPhone(atLimit, "phoneA"));
        Assert.Equal(atLimit, _pb.Text);
        var seq = _stream.Seq;
        Assert.Equal(ClipboardResult.TooLarge, _bridge.ReceiveFromPhone(Of("a", ClipboardBridge.MaxBytes + 1), "phoneA"));
        Assert.Equal(atLimit, _pb.Text);   // refused, not truncated, and nothing written
        Assert.Equal(seq, _stream.Seq);    // nothing published either
    }

    [Fact]
    public void LimitCountsBytesNotCharacters()
    {
        // 40 000 Greek letters are 40 000 characters but 80 000 bytes; 32 768 of them are exactly the limit
        Assert.Equal(ClipboardResult.TooLarge, _bridge.ReceiveFromPhone(Of("λ", 40_000), "phoneA"));
        Assert.Equal(ClipboardResult.Stored, _bridge.ReceiveFromPhone(Of("λ", ClipboardBridge.MaxBytes / 2), "phoneA"));
        Assert.Equal(ClipboardResult.TooLarge, _bridge.ReceiveFromPhone(Of("🌍", ClipboardBridge.MaxBytes / 4 + 1), "phoneA"));
    }

    [Fact]
    public void WhenTheMacRefusesTheWriteNothingIsPublished()
    {
        _pb.Fail = true;
        Assert.Equal(ClipboardResult.Failed, _bridge.ReceiveFromPhone("hello", "phoneA"));
        Assert.Equal(0, _stream.Seq);
    }

    // ---- from the Mac ----

    [Fact]
    public void FirstPollNeverPublishesTheExistingClipboard()
    {
        _pb.Copy("already there");
        _bridge.PollOnce();
        _bridge.PollOnce();
        Assert.Equal(0, _stream.Seq);
    }

    [Fact]
    public void AMacCopyIsPublishedOnceWithNoOrigin()
    {
        _bridge.PollOnce();
        _pb.Copy("copied on the Mac");
        _bridge.PollOnce();
        _bridge.PollOnce();
        var e = Assert.Single(_stream.Since(0, null));
        Assert.Equal(("clipboard", "copied on the Mac", (string?)null), (e.Name, e.Text, e.Origin));
    }

    [Fact]
    public void ConcealedOrNonTextItemsAreNotPublished()
    {
        _bridge.PollOnce();
        _pb.Copy(null);
        _bridge.PollOnce();
        Assert.Equal(0, _stream.Seq);
    }

    [Fact]
    public void AMacCopyOverTheLimitIsNotPublished()
    {
        _bridge.PollOnce();
        _pb.Copy(Of("a", ClipboardBridge.MaxBytes + 1));
        _bridge.PollOnce();
        Assert.Equal(0, _stream.Seq);
    }

    // ---- loop prevention ----

    [Fact]
    public void TextJustReceivedIsNotEchoedBack()
    {
        _bridge.PollOnce();
        _bridge.ReceiveFromPhone("B", "phoneA");   // our own write moves the change counter
        _bridge.PollOnce();
        Assert.DoesNotContain(_stream.Since(0, null), e => e.Origin is null);
        Assert.Single(_stream.Since(0, null));     // only the phone's own event
    }

    [Fact]
    public void ReCopyingAnOlderValueIsSentAgain()
    {
        _bridge.PollOnce();
        _pb.Copy("A");
        _bridge.PollOnce();
        _bridge.ReceiveFromPhone("B", "phone1");
        _bridge.PollOnce();                         // the echo of B: swallowed
        _pb.Copy("A");
        _bridge.PollOnce();                         // the owner copies A again on purpose
        Assert.Equal(["A", "B", "A"], _stream.Since(0, null).Select(e => e.Text));
        Assert.Equal([null, "phone1", null], _stream.Since(0, null).Select(e => e.Origin));
    }

    [Fact]
    public void CopyingTheSameTextTheAgentJustWroteLaterIsSentAgain()
    {
        _bridge.PollOnce();
        _bridge.ReceiveFromPhone("B", "phone1");
        _bridge.PollOnce();                         // echo swallowed, the guard is spent
        _pb.Copy("B");
        _bridge.PollOnce();                         // a deliberate copy of the same words
        Assert.Equal([("B", "phone1"), ("B", null)], _stream.Since(0, null).Select(e => (e.Text, e.Origin)));
    }

    // ---- the switch ----

    [Fact]
    public void NothingIsPublishedAfterTheSwitchIsTurnedOff()
    {
        _bridge.PollOnce();
        SetSwitch(false);
        _pb.Copy("copied while off");
        _bridge.PollOnce();
        Assert.Equal(0, _stream.Seq);
    }

    [Fact]
    public void TurningTheSwitchOnDoesNotPublishWhatIsAlreadyCopied()
    {
        SetSwitch(false);
        _bridge.PollOnce();
        _pb.Copy("copied while off");
        SetSwitch(true);
        _bridge.PollOnce();                         // the first poll after turning on only notes where the clipboard is
        Assert.Equal(0, _stream.Seq);
        _pb.Copy("copied while on");
        _bridge.PollOnce();
        Assert.Equal(["copied while on"], _stream.Since(0, null).Select(e => e.Text));
    }

    [Fact]
    public void ATickThatFailsIsSwallowed()
    {
        _pb.ThrowOnCount = true;
        _bridge.Tick();                             // must not throw: the timer keeps running
        _pb.ThrowOnCount = false;
        _bridge.Tick();
        _pb.Copy("after the failure");
        _bridge.Tick();
        Assert.Equal(["after the failure"], _stream.Since(0, null).Select(e => e.Text));
    }

    // ---- the event list ----

    [Fact]
    public void EventsSkipTheirOriginDevice()
    {
        _stream.Publish("clipboard", "t", "phoneA");
        Assert.Empty(_stream.Since(0, "phoneA"));
        Assert.Single(_stream.Since(0, "phoneB"));
        Assert.Single(_stream.Since(0, null));
    }

    [Fact]
    public void TheStreamKeepsOnlyTheLastTwentyAndNumbersThemInOrder()
    {
        for (var i = 1; i <= 25; i++) _stream.Publish("clipboard", "t" + i, null);
        var all = _stream.Since(0, null);
        Assert.Equal(20, all.Count);
        Assert.Equal(6, all[0].Seq);
        Assert.Equal(25, _stream.Seq);
        Assert.Equal(["t24", "t25"], _stream.Since(23, null).Select(e => e.Text));
    }

    [Fact]
    public void PublishingWakesAListener()
    {
        var woke = 0;
        _stream.Emitted += () => woke++;
        _stream.Publish("clipboard", "t", null);
        Assert.Equal(1, woke);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
