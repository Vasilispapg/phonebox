using PhoneBox.Agent;
using Xunit;

/// <summary>When the macOS menu bar icon is used. The icon itself needs a login session and the main thread, so it is checked by hand.</summary>
public class MacMenuBarTests
{
    private static string? NoEnv(string _) => null;

    [Fact]
    public void ItIsOnInsideTheAppAndOffElsewhere()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.True(MacMenuBar.Wanted([], inBundle: true, NoEnv));
        Assert.False(MacMenuBar.Wanted([], inBundle: false, NoEnv)); // setup.sh / launchd / zip agents keep running without one
        Assert.True(MacMenuBar.Wanted(["--menu-bar"], inBundle: false, NoEnv));
    }

    [Fact]
    public void ItCanBeTurnedOffInsideTheApp()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.False(MacMenuBar.Wanted(["--no-menu-bar"], inBundle: true, NoEnv));
        Assert.False(MacMenuBar.Wanted([], inBundle: true, name => name == "PHONEBOX_NO_MENU_BAR" ? "1" : null));
        Assert.False(MacMenuBar.Wanted(["--menu-bar", "--no-menu-bar"], inBundle: false, NoEnv));
    }

    [Fact]
    public void ItIsNeverUsedOffTheMac()
    {
        if (OperatingSystem.IsMacOS()) return;
        Assert.False(MacMenuBar.Wanted(["--menu-bar"], inBundle: true, NoEnv));
    }
}

public class MacLoginItemTests
{
    [Fact]
    public void TheLaunchAgentOpensTheAppAtLogin()
    {
        var text = MacLoginItem.PlistText("/Users/me/Applications/PhoneBox.app");
        Assert.Contains("<key>Label</key><string>net.oxog.phonebox.app</string>", text);
        Assert.Contains("<string>/usr/bin/open</string><string>-a</string><string>/Users/me/Applications/PhoneBox.app</string>", text);
        Assert.Contains("<key>RunAtLoad</key><true/>", text);
        System.Xml.Linq.XDocument.Parse(text.Replace("<!DOCTYPE", "<!-- ").Replace("dtd\">", "dtd\" -->")); // well-formed
    }

    [Fact]
    public void APathWithMarkupCharactersStaysInsideItsString()
    {
        var text = MacLoginItem.PlistText("/Users/a&b/<x>/PhoneBox.app");
        Assert.Contains("/Users/a&amp;b/&lt;x&gt;/PhoneBox.app", text);
        Assert.DoesNotContain("<x>", text);
    }

    [Fact]
    public void ItDoesNotClashWithTheSetupScriptsLaunchAgent()
    {
        Assert.NotEqual(MacLoginItem.Plist, MacLoginItem.SetupScriptPlist);
        Assert.EndsWith("net.oxog.phonebox.agent.plist", MacLoginItem.SetupScriptPlist); // what scripts/install.sh writes
    }
}
