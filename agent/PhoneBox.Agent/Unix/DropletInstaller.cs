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
        	set command to "PHONEBOX_UI=applet /bin/bash " & quoted form of sendScript
        	repeat with oneFile in theFiles
        		set command to command & " " & quoted form of POSIX path of oneFile
        	end repeat
        	set answer to do shell script command
        	-- the result is shown here, by the app itself and in front: a window made by a faceless osascript can open behind Finder
        	if answer starts with "ALERT:" then
        		activate
        		display alert "PhoneBox" message (text 7 thru -1 of answer) giving up after 30
        	else if answer starts with "NOTE:" then
        		display notification (text 6 thru -1 of answer) with title "PhoneBox"
        	end if
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
            WearThePhoneBoxIcon(app);
            // the script and the icon were added after compiling: sign last, so the seal covers them
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

    /// <summary>
    /// The PhoneBox icon instead of the generic droplet one, so the app in the Dock and in Finder looks like part of PhoneBox. osacompile
    /// also puts the default icon in an asset catalog and names it in Info.plist; either would win over the file, so both go.
    /// </summary>
    private static void WearThePhoneBoxIcon(string app)
    {
        var resources = Path.Combine(app, "Contents", "Resources");
        MakeIcns(Path.Combine(resources, "droplet.icns"));
        var catalog = Path.Combine(resources, "Assets.car");
        if (File.Exists(catalog)) File.Delete(catalog);
        Run("/usr/bin/plutil", "-remove", "CFBundleIconName", Path.Combine(app, "Contents", "Info.plist"));
    }

    /// <summary>Makes an .icns from the PhoneBox icon that is built into the agent (the same one the notifications carry).</summary>
    internal static void MakeIcns(string outFile)
    {
        using var ico = typeof(DropletInstaller).Assembly.GetManifestResourceStream("phonebox.ico") ?? throw new IOException("no PhoneBox icon");
        using var bytes = new MemoryStream();
        ico.CopyTo(bytes);
        var png = NotifierBundle.LargestPng(bytes.ToArray()) ?? throw new IOException("no PNG in the PhoneBox icon");
        var work = Path.Combine(Path.GetTempPath(), "pb-icon-" + Guid.NewGuid().ToString("N"));
        try
        {
            var iconset = Path.Combine(work, "PhoneBox.iconset");
            Directory.CreateDirectory(iconset);
            var big = Path.Combine(work, "icon.png");
            File.WriteAllBytes(big, png);
            foreach (var (file, pixels) in NotifierBundle.IconsetSizes)
                Run("/usr/bin/sips", "-z", pixels.ToString(), pixels.ToString(), big, "--out", Path.Combine(iconset, file));
            Run("/usr/bin/iconutil", "-c", "icns", iconset, "-o", outFile);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
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
