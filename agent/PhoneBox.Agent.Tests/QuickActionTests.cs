using System.Diagnostics;
using System.Text;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The Finder Quick Action "Send to phone" is a small shell script run on the files selected in Finder. Names are data: spaces,
/// quotes, newlines and a leading dash must reach the agent as the file's exact name and never become a command or an option.
/// The real <c>curl</c> and <c>osascript</c> are replaced by recorders, so nothing is sent and nothing is shown.
/// </summary>
public sealed class QuickActionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-qa-" + Guid.NewGuid());
    private string Bin => Path.Combine(_root, "bin");
    private string Files => Path.Combine(_root, "files");
    private string CurlLog => Path.Combine(_root, "curl.log");
    private string NoteLog => Path.Combine(_root, "notes.log");
    private readonly LocalTransferKey _key;

    public QuickActionTests()
    {
        Directory.CreateDirectory(Bin);
        Directory.CreateDirectory(Files);
        _key = new LocalTransferKey(Path.Combine(_root, "transfer.json"), 8765);
        _key.Ensure();
        // a curl that records every argument on its own line (a line per argument, NUL-free) and answers with the status in $FAKE_STATUS
        File.WriteAllText(Path.Combine(Bin, "curl"), "#!/bin/bash\nfor a in \"$@\"; do printf 'ARG:%s\\0' \"$a\" >> \"$CURL_LOG\"; done\nprintf 'END\\0' >> \"$CURL_LOG\"\nprintf '%s' \"${FAKE_STATUS:-202}\"\n");
        File.WriteAllText(Path.Combine(Bin, "osascript"), "#!/bin/bash\nprintf '%s\\n' \"$*\" >> \"$NOTE_LOG\"\n");
        foreach (var tool in new[] { "curl", "osascript" })
            File.SetUnixFileMode(Path.Combine(Bin, tool), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string Script => QuickActionInstaller.BuildScript(_key.Path);

    private string Key => System.Text.Json.JsonDocument.Parse(File.ReadAllText(_key.Path)).RootElement.GetProperty("key").GetString()!;

    private (int Exit, string Curl, string Notes) Run(string script, string status = "202", params string[] files)
    {
        var scriptPath = Path.Combine(_root, "script.sh");
        File.WriteAllText(scriptPath, script);
        var info = new ProcessStartInfo("/bin/bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(scriptPath);
        foreach (var f in files) info.ArgumentList.Add(f);
        info.Environment["PATH"] = Bin + ":/usr/bin:/bin";
        info.Environment["CURL_LOG"] = CurlLog;
        info.Environment["NOTE_LOG"] = NoteLog;
        info.Environment["FAKE_STATUS"] = status;
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20_000));
        return (process.ExitCode, File.Exists(CurlLog) ? File.ReadAllText(CurlLog) : "", File.Exists(NoteLog) ? File.ReadAllText(NoteLog) : "");
    }

    private string Make(string name, string content = "x")
    {
        var path = Path.Combine(Files, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void TheScriptIsValidShellAndTakesItsFilesAsArguments()
    {
        Assert.Contains("\"$@\"", Script);
        var info = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
        info.ArgumentList.Add("-n");
        var path = Path.Combine(_root, "syntax.sh");
        File.WriteAllText(path, Script);
        info.ArgumentList.Add(path);
        using var process = Process.Start(info)!;
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void TheScriptNamesTheKeyFileButNeverHoldsTheKey()
    {
        Assert.Contains(_key.Path, Script);
        Assert.DoesNotContain(Key, Script);
    }

    [Theory]
    [InlineData("a b.txt", "a%20b.txt")]
    [InlineData("it's.txt", "it%27s.txt")]
    [InlineData("-rf.txt", "-rf.txt")]
    [InlineData("say \"hi\".txt", "say%20%22hi%22.txt")]
    [InlineData("line1\nline2.txt", "line1%0Aline2.txt")]
    [InlineData("$(touch pwned).txt", "%24%28touch%20pwned%29.txt")]
    [InlineData("a;b&c|d.txt", "a%3Bb%26c%7Cd.txt")]
    [InlineData("Φωτο.jpg", "%CE%A6%CF%89%CF%84%CE%BF.jpg")]
    public void AnAwkwardNameReachesTheAgentExactlyAndOnlyAsData(string name, string encoded)
    {
        var file = Make(name);
        var result = Run(Script, "202", file);
        Assert.Equal(0, result.Exit);
        var lines = result.Curl.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains($"ARG:http://127.0.0.1:8765/api/transfer/send?name={encoded}", lines);
        Assert.Contains("ARG:" + LocalTransferKey.Header + ": " + Key, lines);
        // the file itself is the upload argument, in one piece
        var upload = Array.IndexOf(lines, "ARG:--upload-file");
        Assert.True(upload >= 0);
        Assert.Equal("ARG:" + file, lines[upload + 1]);
        Assert.False(File.Exists(Path.Combine(Files, "pwned")) || File.Exists("pwned"));
    }

    [Fact]
    public void SeveralFilesAreSentOneByOneAndSummarised()
    {
        var a = Make("a.txt");
        var b = Make("b.txt");
        var result = Run(Script, "202", a, b);
        Assert.Equal(0, result.Exit);
        Assert.Equal(2, result.Curl.Split("END\0", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("2", result.Notes);
    }

    [Fact]
    public void AFolderIsSkipped()
    {
        Directory.CreateDirectory(Path.Combine(Files, "folder"));
        var result = Run(Script, "202", Path.Combine(Files, "folder"));
        Assert.Equal("", result.Curl);
    }

    [Fact]
    public void ARefusedSendSaysWhy()
    {
        var f = Make("a.txt");
        Assert.Contains("off", Run(Script, "403", f).Notes, StringComparison.OrdinalIgnoreCase);
        File.Delete(NoteLog);
        Assert.Contains("large", Run(Script, "413", f).Notes, StringComparison.OrdinalIgnoreCase);
        File.Delete(NoteLog);
        Assert.Contains("full", Run(Script, "507", f).Notes, StringComparison.OrdinalIgnoreCase);
        File.Delete(NoteLog);
        var down = Run(Script, "000", f);
        Assert.NotEqual(0, down.Exit);
        Assert.Contains("running", down.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithoutTheKeyFileNothingIsSent()
    {
        _key.Delete();
        var result = Run(Script, "202", Make("a.txt"));
        Assert.NotEqual(0, result.Exit);
        Assert.Equal("", result.Curl);
        Assert.Contains("off", result.Notes, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the bundle ----

    private string Services => Path.Combine(_root, "Services");

    [Fact]
    public void InstallWritesTheWorkflowAndRemoveDeletesIt()
    {
        var installer = new QuickActionInstaller(Services, _key.Path);
        installer.Install();
        var bundle = Path.Combine(Services, QuickActionInstaller.WorkflowName);
        Assert.True(File.Exists(Path.Combine(bundle, "Contents", "Info.plist")));
        Assert.True(File.Exists(Path.Combine(bundle, "Contents", "document.wflow")));
        Assert.Contains("Send to phone", File.ReadAllText(Path.Combine(bundle, "Contents", "Info.plist")));
        Assert.Contains("runWorkflowAsService", File.ReadAllText(Path.Combine(bundle, "Contents", "Info.plist")));
        Assert.Equal([QuickActionInstaller.WorkflowName], Directory.GetFileSystemEntries(Services).Select(Path.GetFileName)); // no temp folder left
        installer.Remove();
        Assert.False(Directory.Exists(bundle));
        installer.Remove(); // twice is fine
    }

    [Fact]
    public void InstallTwiceReplacesAndNeverTouchesOtherWorkflows()
    {
        Directory.CreateDirectory(Path.Combine(Services, "Mine.workflow"));
        File.WriteAllText(Path.Combine(Services, "Mine.workflow", "keep.txt"), "keep");
        var installer = new QuickActionInstaller(Services, _key.Path);
        installer.Install();
        installer.Install();
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Services, "Mine.workflow", "keep.txt")));
        Assert.Equal(2, Directory.GetFileSystemEntries(Services).Length);
        installer.Remove();
        Assert.True(File.Exists(Path.Combine(Services, "Mine.workflow", "keep.txt")));
    }

    [Fact]
    public void TheBundleHoldsTheScriptAndThePlistsAreValid()
    {
        if (!File.Exists("/usr/bin/plutil")) return; // macOS only
        var installer = new QuickActionInstaller(Services, _key.Path);
        installer.Install();
        var contents = Path.Combine(Services, QuickActionInstaller.WorkflowName, "Contents");
        foreach (var plist in new[] { "Info.plist", "document.wflow" })
        {
            var info = new ProcessStartInfo("/usr/bin/plutil") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-lint");
            info.ArgumentList.Add(Path.Combine(contents, plist));
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10_000));
            Assert.True(process.ExitCode == 0, plist + ": " + output);
        }
        var extract = new ProcessStartInfo("/usr/bin/plutil") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-extract", "actions.0.action.ActionParameters.COMMAND_STRING", "raw", "-o", "-", Path.Combine(contents, "document.wflow") })
            extract.ArgumentList.Add(a);
        using var p = Process.Start(extract)!;
        var embedded = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(10_000));
        Assert.Equal(Script.TrimEnd('\n'), embedded.TrimEnd('\n'));
    }

    [Fact]
    public void ARefreshHookRunsAfterAnInstallAndARemoval()
    {
        var calls = 0;
        var installer = new QuickActionInstaller(Services, _key.Path, () => calls++);
        installer.Install();
        installer.Remove();
        Assert.Equal(2, calls);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
