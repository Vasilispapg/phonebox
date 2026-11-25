using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>
/// "Send to phone.app" in <c>~/Applications</c>: the second way to send from the Mac besides the Finder Quick Action. It is a small
/// AppleScript droplet, so files can be dropped on its icon, on the Dock or on a Finder toolbar (drag it there once), and opening it
/// asks which files to send. Both hand the files to the same script the Quick Action runs, as quoted arguments. Built with osacompile,
/// signed ad hoc, and moved into place whole; nothing else in the folder is touched.
/// </summary>
internal sealed class DropletInstaller(string appsDir, string keyFilePath) : IQuickActionInstaller
{
    public const string AppName = "Send to phone.app";

    /// <summary>The applet. A file reaches the shell only as <c>quoted form of</c> its POSIX path, an argument of the send script.</summary>
    public const string AppleScript = """
        on run
        	try
        		set picked to choose file with prompt "Choose the files to send to your phone" with multiple selections allowed
        	on error
        		return
        	end try
        	my sendAll(picked)
        end run

        on open theFiles
        	my sendAll(theFiles)
        end open

        on sendAll(theFiles)
        	set sendScript to POSIX path of (path to resource "send.sh")
        	set command to "/bin/bash " & quoted form of sendScript
        	repeat with oneFile in theFiles
        		set command to command & " " & quoted form of POSIX path of oneFile
        	end repeat
        	do shell script command
        end sendAll
        """;

    public void Install()
    {
        Directory.CreateDirectory(appsDir);
        var temp = Path.Combine(appsDir, ".pb-droplet-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(temp);
            var source = Path.Combine(temp, "droplet.applescript");
            File.WriteAllText(source, AppleScript);
            var app = Path.Combine(temp, AppName);
            Run("/usr/bin/osacompile", "-o", app, source);
            var script = Path.Combine(app, "Contents", "Resources", "send.sh");
            File.WriteAllText(script, QuickActionInstaller.BuildScript(keyFilePath));
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            // the script was added after compiling: sign last, so the seal covers it
            Run("/usr/bin/codesign", "--force", "--deep", "-s", "-", app);
            var target = Path.Combine(appsDir, AppName);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(app, target);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    public void Remove()
    {
        var target = Path.Combine(appsDir, AppName);
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }

    private static void Run(string tool, params string[] args)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new IOException(tool + " did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000)) { try { process.Kill(); } catch (InvalidOperationException) { } throw new IOException(tool + " timed out"); }
        if (process.ExitCode != 0) throw new IOException(tool + " failed: " + error.Result.Trim() + output.Result.Trim());
    }
}
