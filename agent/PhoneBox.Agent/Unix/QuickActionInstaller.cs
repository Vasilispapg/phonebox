using System.Diagnostics;
using System.Security;

namespace PhoneBox.Agent;

/// <summary>
/// The Finder Quick Action "Send to phone": a small Automator workflow in <c>~/Library/Services</c> that runs one shell script on the
/// files selected in Finder (right-click › Quick Actions). The script sends each file to the agent's local trigger with the local
/// key; a file name is only ever a quoted argument and a percent-encoded part of the address, never part of a command. The
/// workflow is written next to its final place and moved in, so Finder never sees half of it, and nothing else in the folder is touched.
/// </summary>
internal sealed class QuickActionInstaller(string servicesDir, string keyFilePath, Action? refresh = null) : IQuickActionInstaller
{
    public const string WorkflowName = "Send to phone.workflow";

    public void Install()
    {
        Directory.CreateDirectory(servicesDir);
        var temp = Path.Combine(servicesDir, ".pb-quickaction-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var contents = Path.Combine(temp, "Contents");
            Directory.CreateDirectory(contents);
            File.WriteAllText(Path.Combine(contents, "Info.plist"), InfoPlist());
            File.WriteAllText(Path.Combine(contents, "document.wflow"), WorkflowPlist(BuildScript(keyFilePath)));
            var target = Path.Combine(servicesDir, WorkflowName);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(temp, target);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (IOException) { }
        }
        refresh?.Invoke();
    }

    public void Remove()
    {
        var target = Path.Combine(servicesDir, WorkflowName);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        refresh?.Invoke();
    }

    /// <summary>Asks macOS to look at the Services folder again; best effort, never throws.</summary>
    public static void RefreshServices()
    {
        try
        {
            var pbs = "/System/Library/CoreServices/pbs";
            if (!File.Exists(pbs)) return;
            var info = new ProcessStartInfo(pbs) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-update");
            using var process = Process.Start(info);
            if (process is not null && !process.WaitForExit(5000)) process.Kill();
        }
        catch (Exception) { /* the workflow shows up on its own a little later */ }
    }

    /// <summary>
    /// The script the workflow runs. It reads the port and the key from <paramref name="keyFilePath"/> (the key is never written
    /// into it), skips folders, sends every file with <c>curl --globoff --upload-file</c> (without --globoff curl reads [ ] and { } in a path as a pattern), and says in one notification how it went.
    /// Names are percent-encoded byte by byte (od), so no name can reach a shell, an AppleScript or a curl option.
    /// </summary>
    public static string BuildScript(string keyFilePath) => $$"""
        #!/bin/bash
        # "Send to phone" (PhoneBox): sends the files selected in Finder to the paired phone through the local PhoneBox agent.
        KEYFILE='{{keyFilePath.Replace("'", "'\\''")}}'
        say() { osascript -e 'on run argv' -e 'display notification (item 1 of argv) with title "PhoneBox"' -e 'end run' -- "$1" >/dev/null 2>&1; }
        if [ ! -r "$KEYFILE" ]; then say "Sending files to the phone is off"; exit 1; fi
        port=$(sed -n 's/.*"port":\([0-9][0-9]*\).*/\1/p' "$KEYFILE")
        key=$(sed -n 's/.*"key":"\([^"]*\)".*/\1/p' "$KEYFILE")
        if [ -z "$port" ] || [ -z "$key" ]; then say "Sending files to the phone is off"; exit 1; fi
        encode() {
          local h out=""
          for h in $(printf '%s' "$1" | od -An -v -tx1); do
            case "$h" in
              2d|2e|5f|7e|3[0-9]|4[1-9a-f]|5[0-9a]|6[1-9a-f]|7[0-9a]) out+=$(printf "\\x$h") ;;
              *) out+="%$(printf '%s' "$h" | tr 'a-f' 'A-F')" ;;
            esac
          done
          printf '%s' "$out"
        }
        sent=0
        problem=""
        for f in "$@"; do
          [ -f "$f" ] || continue
          name=$(basename -- "$f")
          code=$(curl --globoff --silent --connect-timeout 5 --output /dev/null --write-out '%{http_code}' -H "X-PhoneBox-Transfer-Key: $key" --upload-file "$f" "http://127.0.0.1:$port/api/transfer/send?name=$(encode "$name")")
          case "$code" in
            202) sent=$((sent + 1)) ;;
            403) problem="Sending files to the phone is off"; break ;;
            413) problem="That file is too large (the limit is 2 GB)"; break ;;
            507) problem="The outbox is full; try again in a while"; break ;;
            *) problem="PhoneBox is not running"; break ;;
          esac
        done
        if [ -n "$problem" ]; then say "$problem"; exit 1; fi
        if [ "$sent" -gt 0 ]; then say "Sent $sent file(s) to the phone"; fi
        exit 0
        """;

    private static string InfoPlist() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
        	<key>NSServices</key>
        	<array>
        		<dict>
        			<key>NSBackgroundColorName</key>
        			<string>background</string>
        			<key>NSIconName</key>
        			<string>NSActionTemplate</string>
        			<key>NSMenuItem</key>
        			<dict>
        				<key>default</key>
        				<string>Send to phone</string>
        			</dict>
        			<key>NSMessage</key>
        			<string>runWorkflowAsService</string>
        			<key>NSRequiredContext</key>
        			<dict>
        				<key>NSApplicationIdentifier</key>
        				<string>com.apple.finder</string>
        			</dict>
        			<key>NSSendFileTypes</key>
        			<array>
        				<string>public.item</string>
        			</array>
        		</dict>
        	</array>
        </dict>
        </plist>
        """;

    /// <summary>An Automator workflow of one action, "Run Shell Script" with the files as arguments, for the Finder's Quick Actions.</summary>
    private static string WorkflowPlist(string script) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
        	<key>AMApplicationBuild</key>
        	<string>523</string>
        	<key>AMApplicationVersion</key>
        	<string>2.10</string>
        	<key>AMDocumentVersion</key>
        	<string>2</string>
        	<key>actions</key>
        	<array>
        		<dict>
        			<key>action</key>
        			<dict>
        				<key>AMAccepts</key>
        				<dict>
        					<key>Container</key>
        					<string>List</string>
        					<key>Optional</key>
        					<true/>
        					<key>Types</key>
        					<array>
        						<string>com.apple.cocoa.path</string>
        					</array>
        				</dict>
        				<key>AMActionVersion</key>
        				<string>2.0.3</string>
        				<key>AMApplication</key>
        				<array>
        					<string>Automator</string>
        				</array>
        				<key>AMParameterProperties</key>
        				<dict>
        					<key>COMMAND_STRING</key>
        					<dict/>
        					<key>CheckedForUserDefaultShell</key>
        					<dict/>
        					<key>inputMethod</key>
        					<dict/>
        					<key>shell</key>
        					<dict/>
        					<key>source</key>
        					<dict/>
        				</dict>
        				<key>AMProvides</key>
        				<dict>
        					<key>Container</key>
        					<string>List</string>
        					<key>Types</key>
        					<array>
        						<string>com.apple.cocoa.path</string>
        					</array>
        				</dict>
        				<key>ActionBundlePath</key>
        				<string>/System/Library/Automator/Run Shell Script.action</string>
        				<key>ActionName</key>
        				<string>Run Shell Script</string>
        				<key>ActionParameters</key>
        				<dict>
        					<key>COMMAND_STRING</key>
        					<string>{{SecurityElement.Escape(script)}}</string>
        					<key>CheckedForUserDefaultShell</key>
        					<true/>
        					<key>inputMethod</key>
        					<integer>1</integer>
        					<key>shell</key>
        					<string>/bin/bash</string>
        					<key>source</key>
        					<string></string>
        				</dict>
        				<key>BundleIdentifier</key>
        				<string>com.apple.RunShellScript</string>
        				<key>CFBundleVersion</key>
        				<string>2.0.3</string>
        				<key>CanShowSelectedItemsWhenRun</key>
        				<false/>
        				<key>CanShowWhenRun</key>
        				<true/>
        				<key>Category</key>
        				<array>
        					<string>AMCategoryUtilities</string>
        				</array>
        				<key>Class Name</key>
        				<string>RunShellScriptAction</string>
        				<key>InputUUID</key>
        				<string>6C0C43E1-5A53-4A7B-9E0B-1B1B5C6A0C01</string>
        				<key>Keywords</key>
        				<array>
        					<string>Shell</string>
        					<string>Script</string>
        					<string>Command</string>
        					<string>Run</string>
        					<string>Unix</string>
        				</array>
        				<key>OutputUUID</key>
        				<string>6C0C43E1-5A53-4A7B-9E0B-1B1B5C6A0C02</string>
        				<key>UUID</key>
        				<string>6C0C43E1-5A53-4A7B-9E0B-1B1B5C6A0C03</string>
        				<key>UnlocalizedApplications</key>
        				<array>
        					<string>Automator</string>
        				</array>
        				<key>isViewVisible</key>
        				<integer>1</integer>
        				<key>location</key>
        				<string>309.000000:253.000000</string>
        				<key>nibPath</key>
        				<string>/System/Library/Automator/Run Shell Script.action/Contents/Resources/Base.lproj/main.nib</string>
        			</dict>
        			<key>isViewVisible</key>
        			<integer>1</integer>
        		</dict>
        	</array>
        	<key>connectors</key>
        	<dict/>
        	<key>workflowMetaData</key>
        	<dict>
        		<key>applicationBundleIDsByPath</key>
        		<dict/>
        		<key>applicationPaths</key>
        		<array/>
        		<key>inputTypeIdentifier</key>
        		<string>com.apple.Automator.fileSystemObject</string>
        		<key>outputTypeIdentifier</key>
        		<string>com.apple.Automator.nothing</string>
        		<key>presentationMode</key>
        		<integer>15</integer>
        		<key>processesInput</key>
        		<integer>0</integer>
        		<key>serviceApplicationBundleID</key>
        		<string>com.apple.finder</string>
        		<key>serviceInputTypeIdentifier</key>
        		<string>com.apple.Automator.fileSystemObject</string>
        		<key>serviceOutputTypeIdentifier</key>
        		<string>com.apple.Automator.nothing</string>
        		<key>serviceProcessesInput</key>
        		<integer>0</integer>
        		<key>systemImageName</key>
        		<string>NSActionTemplate</string>
        		<key>useAutomaticInputType</key>
        		<integer>0</integer>
        		<key>workflowTypeIdentifier</key>
        		<string>com.apple.Automator.servicesMenu</string>
        	</dict>
        </dict>
        </plist>
        """;
}
