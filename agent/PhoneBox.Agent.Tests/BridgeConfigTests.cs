using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The phone bridge moves clipboard text off this computer, so every switch is off unless the owner turns it on, in the
/// code and in each default config file, and the phone can read the switch from the public config.
/// </summary>
public sealed class BridgeConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-bridge-cfg-" + Guid.NewGuid());

    [Fact]
    public void ClipboardIsOffByDefault() => Assert.False(new BridgeConfig().Clipboard);

    [Theory]
    [InlineData("config.default.json")]
    [InlineData("config.default.linux.json")]
    [InlineData("config.default.macos.json")]
    public void EveryDefaultConfigDeclaresTheBridgeOff(string name)
    {
        using var stream = typeof(AgentConfig).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var doc = JsonDocument.Parse(stream, new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("bridge").GetProperty("clipboard").ValueKind);
    }

    [Fact]
    public void PublicConfigCarriesTheBridgeFlags()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """{"bridge":{"clipboard":true}}""");
        var json = JsonSerializer.Serialize(new ConfigStore(path).Get().ToPublic(), ConfigStore.WriteOpts);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.True, doc.RootElement.GetProperty("bridge").GetProperty("clipboard").ValueKind);
    }

    [Fact]
    public void PublicConfigShowsTheBridgeOffForAFreshInstall()
    {
        var json = JsonSerializer.Serialize(new AgentConfig().ToPublic(), ConfigStore.WriteOpts);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("bridge").GetProperty("clipboard").ValueKind);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
