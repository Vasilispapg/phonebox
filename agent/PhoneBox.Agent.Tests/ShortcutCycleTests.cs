using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>A cycle key runs the next key of its list on each tap, and remembers which ran last.</summary>
public sealed class ShortcutCycleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-cycle-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // a toggle remembers its state in the agent, which is how a test sees which key ran (ids are unique: the memory is static)
    private static Shortcut Lamp(string id) => new() { Id = id, Label = id, Toggle = true, Cmd = "builtin:macro", Steps = ["wait:1"] };
    private static Shortcut Cycle(string id, params string[] keys) => new() { Id = id, Label = id, Cmd = "builtin:cycle", Cycle = [.. keys] };
    private static bool On(Shortcut s) => Shortcuts.State(s, null, null) == true;
    private static string Id() => "cy-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void EachTapRunsTheNextKeyAndTheListStartsAgain()
    {
        var id = Id();
        Shortcut a = Lamp(id + "a"), b = Lamp(id + "b"), c = Lamp(id + "c"), key = Cycle(id, a.Id, b.Id, c.Id);
        var cfg = new AgentConfig { Shortcuts = [a, b, c, key] };
        foreach (var expected in new[] { a, b, c, a })
        {
            var before = new[] { a, b, c }.ToDictionary(s => s.Id, On);
            Assert.Null(Shortcuts.Run(cfg, id, NullLogger.Instance));
            foreach (var s in new[] { a, b, c }) Assert.Equal(s == expected ? !before[s.Id] : before[s.Id], On(s));
        }
    }

    [Fact]
    public void AKeyThatFailsIsTriedAgainOnTheNextTap()
    {
        var id = Id();
        var bad = new Shortcut { Id = id + "bad", Label = "bad", Cmd = "builtin:keys", Args = "not_a_key" };
        var good = Lamp(id + "good");
        var key = Cycle(id, bad.Id, good.Id);
        var cfg = new AgentConfig { Shortcuts = [bad, good, key] };
        Assert.NotNull(Shortcuts.Run(cfg, id, NullLogger.Instance));
        Assert.Null(Shortcuts.CycleStates(cfg));
        Assert.NotNull(Shortcuts.Run(cfg, id, NullLogger.Instance)); // still the failing key, not the next one
        Assert.False(On(good));
    }

    [Fact]
    public void ThePhoneIsToldWhichKeyRanLast()
    {
        var id = Id();
        Shortcut a = Lamp(id + "a"), b = Lamp(id + "b"), key = Cycle(id, a.Id, b.Id);
        var cfg = new AgentConfig { Shortcuts = [a, b, key] };
        Assert.Null(Shortcuts.CycleStates(cfg)); // never tapped
        Shortcuts.Run(cfg, id, NullLogger.Instance);
        Assert.Equal(0, Shortcuts.CycleStates(cfg)![id]);
        Shortcuts.Run(cfg, id, NullLogger.Instance);
        Assert.Equal(1, Shortcuts.CycleStates(cfg)![id]);
        key.Cycle = [a.Id]; // the list shrank since: a stale position is not sent
        Assert.Null(Shortcuts.CycleStates(cfg));
    }

    [Fact]
    public void EditingTheListStartsTheCycleOver()
    {
        var id = Id();
        Shortcut a = Lamp(id + "a"), b = Lamp(id + "b"), key = Cycle(id, a.Id, b.Id);
        var cfg = new AgentConfig { Shortcuts = [a, b, key] };
        Shortcuts.Run(cfg, id, NullLogger.Instance);
        Shortcuts.Run(cfg, id, NullLogger.Instance);
        Assert.Equal(1, Shortcuts.CycleStates(cfg)![id]); // b ran last
        key.Cycle = [b.Id, a.Id];                           // the same keys, in the other order
        Assert.Null(Shortcuts.CycleStates(cfg));            // position 1 would now mean a: not sent
        var bBefore = On(b);
        Shortcuts.Run(cfg, id, NullLogger.Instance);        // starts from the first key of the new list
        Assert.Equal(0, Shortcuts.CycleStates(cfg)![id]);
        Assert.Equal(!bBefore, On(b));
    }

    [Fact]
    public void KeysThatCycleIntoEachOtherStopInsteadOfRunningForever()
    {
        var id = Id();
        Shortcut a = Cycle(id + "a", id + "b", id + "b"), b = Cycle(id + "b", id + "a", id + "a");
        var cfg = new AgentConfig { Shortcuts = [a, b] };
        Assert.NotNull(Shortcuts.Run(cfg, a.Id, NullLogger.Instance));
    }

    [Fact]
    public void AMacroCanRunACycleKey()
    {
        var id = Id();
        Shortcut a = Lamp(id + "a"), b = Lamp(id + "b"), key = Cycle(id, a.Id, b.Id);
        var macro = new Shortcut { Id = id + "m", Label = "m", Cmd = "builtin:macro", Steps = [$"shortcut:{id}", $"shortcut:{id}"] };
        var cfg = new AgentConfig { Shortcuts = [a, b, key, macro] };
        Assert.Null(Shortcuts.Run(cfg, macro.Id, NullLogger.Instance));
        Assert.True(On(a));
        Assert.True(On(b));
    }

    [Fact]
    public void SavingKeepsOnlyTheKeysThatExist_AndOnlyForCycleKeys()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        var json = """
            { "shortcuts": [
              { "id": "a", "label": "A", "cmd": "builtin:lock" },
              { "id": "b", "label": "B", "cmd": "builtin:lock" },
              { "id": "c", "label": "C", "cmd": "builtin:cycle", "cycle": [" a ", "gone", "c", "b", "a"] },
              { "id": "d", "label": "D", "cmd": "builtin:lock", "cycle": ["a", "b"] } ] }
            """;
        Assert.Null(store.Save(json));
        var keys = store.Get().Shortcuts.ToDictionary(s => s.Id);
        Assert.Equal(["a", "b", "a"], keys["c"].Cycle);
        Assert.Null(keys["d"].Cycle);
    }

    [Fact]
    public void ACycleKeyNeedsAtLeastTwoKeys()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config2.json"));
        var error = store.Save("""{ "shortcuts": [ { "id": "a", "label": "A", "cmd": "builtin:lock" }, { "id": "c", "label": "Cycle", "cmd": "builtin:cycle", "cycle": ["a", "missing"] } ] }""");
        Assert.NotNull(error);
        Assert.Contains("Cycle", error);
    }

    [Fact]
    public void ThePhoneGetsTheListOfAKeyThatCyclesAndNothingForOtherKeys()
    {
        var cfg = new AgentConfig { Shortcuts = [Lamp("x"), Lamp("y"), Cycle("z", "x", "y")] };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(cfg.ToPublic()));
        var keys = doc.RootElement.GetProperty("shortcuts").EnumerateArray().ToDictionary(k => k.GetProperty("Id").GetString()!);
        Assert.Equal(["x", "y"], keys["z"].GetProperty("cycle").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(JsonValueKind.Null, keys["x"].GetProperty("cycle").ValueKind);
    }
}
