using System.Text;
using PhoneBox.Agent;
using Xunit;

/// <summary>A name that arrives from the phone is data from another device: it must land as a harmless file inside one folder.</summary>
public sealed class ReceivedFileNameTests
{
    [Theory]
    [InlineData("../../.ssh/authorized_keys", "authorized_keys")]
    [InlineData("..", "file")]
    [InlineData("....", "file")]
    [InlineData("a/b\\c.txt", "c.txt")]
    [InlineData("x\0y\n.jpg", "xy.jpg")]
    [InlineData(".hidden.png", "hidden.png")]
    [InlineData("  spaced.txt  ", "spaced.txt")]
    [InlineData("/", "file")]
    [InlineData("", "file")]
    [InlineData(null, "file")]
    public void Sanitize(string? raw, string expected) => Assert.Equal(expected, ReceivedFileName.Sanitize(raw));

    [Fact]
    public void ALongNameIsCutToTheLimitAndKeepsItsExtension()
    {
        var name = ReceivedFileName.Sanitize(new string('a', 5000) + ".jpg");
        Assert.True(Encoding.UTF8.GetByteCount(name) <= ReceivedFileName.MaxBytes);
        Assert.EndsWith(".jpg", name);
    }

    [Fact]
    public void ACutNeverSplitsAnEmojiPair()
    {
        var name = ReceivedFileName.Sanitize(string.Concat(Enumerable.Repeat("😀", 200)) + ".png");
        Assert.True(Encoding.UTF8.GetByteCount(name) <= ReceivedFileName.MaxBytes);
        // a split surrogate pair would not survive a round trip through UTF-8
        Assert.Equal(name, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(name)));
        Assert.DoesNotContain('�', name);
    }

    [Fact]
    public void ATakenNameGetsANumber()
    {
        var taken = new HashSet<string> { "a.txt" };
        Assert.Equal("a (1).txt", ReceivedFileName.Unique("/x", "a.txt", taken.Contains));
        taken.Add("a (1).txt");
        Assert.Equal("a (2).txt", ReceivedFileName.Unique("/x", "a.txt", taken.Contains));
    }

    [Fact]
    public void AFreeNameIsKept() => Assert.Equal("a.txt", ReceivedFileName.Unique("/x", "a.txt", _ => false));

    [Fact]
    public void ANameWithoutAnExtensionGetsTheNumberAtTheEnd()
    {
        var taken = new HashSet<string> { "notes" };
        Assert.Equal("notes (1)", ReceivedFileName.Unique("/x", "notes", taken.Contains));
    }
}
