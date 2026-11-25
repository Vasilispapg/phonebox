using System.Diagnostics;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// "Send to phone.app", the second way to send from the Mac besides the Finder Quick Action: an AppleScript droplet. Files dropped on
/// it (on the Dock, on a Finder toolbar, on the icon) or chosen when it is opened go to the same script the Quick Action runs.
/// Needs osacompile and codesign, so these run on a Mac only.
/// </summary>
public sealed class DropletTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-droplet-" + Guid.NewGuid());
    private string Apps => Path.Combine(_root, "Applications");
    private string KeyFile => Path.Combine(_root, "transfer.json");
    private static bool OnMac => File.Exists("/usr/bin/osacompile") && File.Exists("/usr/bin/codesign");

    public DropletTests() => Directory.CreateDirectory(_root);

    private string Bundle => Path.Combine(Apps, DropletInstaller.AppName);

    private static (int Exit, string Output) Tool(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000));
        return (process.ExitCode, output);
    }

    [Fact]
    public void InstallBuildsAnAppThatHoldsTheSendScript()
    {
        if (!OnMac) return;
        new DropletInstaller(Apps, KeyFile).Install();
        Assert.True(Directory.Exists(Bundle));
        var script = File.ReadAllText(Path.Combine(Bundle, "Contents", "Resources", "send.sh"));
        Assert.Equal(QuickActionInstaller.BuildScript(KeyFile), script);
        Assert.Contains("--globoff", script);
        Assert.False(Directory.GetFileSystemEntries(Apps).Any(p => p.Contains(".tmp"))); // no half-built copy left
    }

    [Fact]
    public void TheAppAcceptsDroppedFilesAndHasAValidSignature()
    {
        if (!OnMac) return;
        new DropletInstaller(Apps, KeyFile).Install();
        var plist = File.ReadAllText(Path.Combine(Bundle, "Contents", "Info.plist"));
        Assert.Contains("CFBundleDocumentTypes", plist); // an applet with an "on open" handler is a droplet
        var verify = Tool("/usr/bin/codesign", "--verify", "--deep", "--strict", Bundle);
        Assert.True(verify.Exit == 0, verify.Output); // the script was added after compiling: the seal must cover it
    }

    [Fact]
    public void TheAppWearsThePhoneBoxIconNotTheGenericDropletOne()
    {
        if (!OnMac) return;
        new DropletInstaller(Apps, KeyFile).Install();
        var resources = Path.Combine(Bundle, "Contents", "Resources");
        var icon = File.ReadAllBytes(Path.Combine(resources, "droplet.icns"));
        Assert.Equal("icns", System.Text.Encoding.ASCII.GetString(icon, 0, 4));
        // the same icon the installer makes from the PhoneBox icon, not the default one osacompile puts in
        var expected = Path.Combine(_root, "expected.icns");
        DropletInstaller.MakeIcns(expected);
        Assert.Equal(File.ReadAllBytes(expected), icon);
        // an asset catalog or an icon name would win over the file: neither is left
        Assert.False(File.Exists(Path.Combine(resources, "Assets.car")));
        Assert.DoesNotContain("CFBundleIconName", File.ReadAllText(Path.Combine(Bundle, "Contents", "Info.plist")));
        var verify = Tool("/usr/bin/codesign", "--verify", "--deep", "--strict", Bundle);
        Assert.True(verify.Exit == 0, verify.Output);
    }

    [Fact]
    public void TheAppletPassesFilesAsQuotedArgumentsToTheScript()
    {
        if (!OnMac) return;
        // the AppleScript source is data in the installer: files reach the shell only as 'quoted form of' paths
        var source = DropletInstaller.AppleScript;
        Assert.Contains("on open", source);
        Assert.Contains("on run", source);
        Assert.Contains("quoted form of POSIX path of", source);
        Assert.Contains("path to resource \"send.sh\"", source);
        Assert.DoesNotContain("do shell script \"/bin/bash -c", source);
        // the result is shown by the applet itself, in front: a window from a faceless osascript can open behind Finder
        Assert.Contains("PHONEBOX_UI=applet", source);
        Assert.Contains("activate", source);
        Assert.Contains("display alert", source);
        Assert.Contains("display notification", source);
    }

    [Fact]
    public void InstallTwiceReplacesAndRemoveDeletesWithoutTouchingOtherApps()
    {
        if (!OnMac) return;
        Directory.CreateDirectory(Path.Combine(Apps, "Other.app"));
        File.WriteAllText(Path.Combine(Apps, "Other.app", "keep"), "keep");
        var installer = new DropletInstaller(Apps, KeyFile);
        installer.Install();
        installer.Install();
        Assert.Equal(2, Directory.GetFileSystemEntries(Apps).Length);
        installer.Remove();
        Assert.False(Directory.Exists(Bundle));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Apps, "Other.app", "keep")));
        installer.Remove(); // twice is fine
    }

    [Fact]
    public void RemoveWithoutAnAppDoesNothing()
    {
        new DropletInstaller(Apps, KeyFile).Remove();
        Assert.False(Directory.Exists(Apps)); // not even the folder is made
    }

    [Fact]
    public void BothWaysToSendAreInstalledAndRemovedTogether()
    {
        var calls = new List<string>();
        var combined = new CombinedQuickAction(new Recording("quick", calls), new Recording("droplet", calls));
        combined.Install();
        combined.Remove();
        Assert.Equal(["quick:install", "droplet:install", "quick:remove", "droplet:remove"], calls);
    }

    [Fact]
    public void AFailureOfOneDoesNotKeepTheOtherFromRunningAndIsStillReported()
    {
        var calls = new List<string>();
        var combined = new CombinedQuickAction(new Recording("quick", calls, fail: true), new Recording("droplet", calls));
        Assert.Throws<IOException>(() => combined.Install());
        Assert.Equal(["quick:install", "droplet:install"], calls); // the droplet was still tried
    }

    private sealed class Recording(string name, List<string> calls, bool fail = false) : IQuickActionInstaller
    {
        public void Install() { calls.Add(name + ":install"); if (fail) throw new IOException(name); }
        public void Remove() => calls.Add(name + ":remove");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
