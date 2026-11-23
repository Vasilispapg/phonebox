using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>
/// macOS: a menu bar icon (phone symbol and a menu: status, settings, updates, restart, log, start at login, quit) while the agent runs as PhoneBox.app. It talks to AppKit
/// through the Objective-C runtime from the agent's own process, so there is still one binary with one code signature
/// (the Accessibility permission belongs to that identity, see sign-macos-agent). AppKit needs the process's main thread:
/// Program runs the web host on a worker thread and gives the main thread to <see cref="Run"/>.
/// It is on only inside PhoneBox.app (or with --menu-bar): the agent started by setup.sh / launchd or over SSH has no
/// menu bar, and AppKit aborts the process without a login session, which is checked first.
/// </summary>
internal static class MacMenuBar
{
    /// <summary>Inside PhoneBox.app, or asked for with --menu-bar; --no-menu-bar and PHONEBOX_NO_MENU_BAR turn it off.</summary>
    internal static bool Wanted(string[] args, bool inBundle, Func<string, string?> env) =>
        OperatingSystem.IsMacOS()
        && !args.Contains("--no-menu-bar")
        && string.IsNullOrEmpty(env("PHONEBOX_NO_MENU_BAR"))
        && (inBundle || args.Contains("--menu-bar"));

    private const string Lib = "/usr/lib/libobjc.A.dylib";
    [DllImport(Lib)] private static extern IntPtr objc_getClass(string name);
    [DllImport(Lib)] private static extern IntPtr sel_registerName(string name);
    [DllImport(Lib)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(Lib)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(Lib)] private static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Lib)] private static extern void objc_autoreleasePoolPop(IntPtr pool);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr sel, double a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr SendEvent(IntPtr self, IntPtr sel, ulong mask, IntPtr date, IntPtr mode, sbyte dequeue);
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern int pthread_main_np();
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")] private static extern IntPtr CGSessionCopyCurrentDictionary();
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr cf);

    private delegate void Action3(IntPtr self, IntPtr sel, IntPtr sender);
    // kept in fields: the Objective-C runtime holds only the raw function pointers
    private static Action3? _clicked;
    private static volatile bool _quitting;

    private static IntPtr Cls(string name) => objc_getClass(name);
    private static IntPtr Sel(string name) => sel_registerName(name);
    private static IntPtr Str(string text)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try { return Send(Cls("NSString"), Sel("stringWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    /// <summary>True when this process may use AppKit: on the main thread, in a logged-in GUI session.</summary>
    internal static bool CanRun()
    {
        if (pthread_main_np() == 0) return false;
        var session = CGSessionCopyCurrentDictionary(); // null without a login session (SSH, a daemon): AppKit would abort
        if (session == IntPtr.Zero) return false;
        CFRelease(session);
        return true;
    }

    /// <summary>One line of the menu. Dynamic parts are re-read about once a second while the menu is closed.</summary>
    internal sealed record MenuEntry(string Title, Action? Click = null, Func<string>? DynamicTitle = null, Func<bool>? Visible = null,
        Func<bool>? Checked = null, Func<bool>? Enabled = null, string Key = "")
    {
        public static readonly MenuEntry Separator = new("-");
        public bool IsSeparator => Title == "-";
    }

    private static MenuEntry[] _entries = [];

    /// <summary>
    /// Shows the icon and runs the main thread's event loop until <paramref name="hostStopped"/> says the agent is
    /// shutting down. A click runs the entry's action on a worker thread, so a slow one never freezes the menu bar.
    /// </summary>
    internal static void Run(MenuEntry[] entries, Func<bool> hostStopped)
    {
        _entries = entries;
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        var app = Send(Cls("NSApplication"), Sel("sharedApplication"));
        Send(app, Sel("setActivationPolicy:"), (IntPtr)1); // accessory: no Dock icon, no menu bar of its own

        _clicked = (_, _, sender) =>
        {
            var index = (int)Send(sender, Sel("tag"));
            if (index < 0 || index >= _entries.Length || _entries[index].Click is not { } click) return;
            _ = Task.Run(() => Guard(click));
        };
        var target = Cls("PhoneBoxMenuTarget");
        if (target == IntPtr.Zero)
        {
            var created = objc_allocateClassPair(Cls("NSObject"), "PhoneBoxMenuTarget", 0);
            class_addMethod(created, Sel("menuClicked:"), Marshal.GetFunctionPointerForDelegate(_clicked), "v@:@");
            objc_registerClassPair(created);
            target = created;
        }
        var handler = Send(Send(target, Sel("alloc")), Sel("init"));

        var menu = Send(Send(Cls("NSMenu"), Sel("alloc")), Sel("init"));
        Send(menu, Sel("setAutoenablesItems:"), IntPtr.Zero); // we set enabled ourselves
        var items = new IntPtr[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.IsSeparator) items[i] = Send(Cls("NSMenuItem"), Sel("separatorItem"));
            else
            {
                items[i] = Send(Send(Cls("NSMenuItem"), Sel("alloc")), Sel("initWithTitle:action:keyEquivalent:"),
                    Str(entry.Title), entry.Click is null ? IntPtr.Zero : Sel("menuClicked:"), Str(entry.Key));
                Send(items[i], Sel("setTag:"), (IntPtr)i);
                if (entry.Click is not null) Send(items[i], Sel("setTarget:"), handler);
            }
            Send(menu, Sel("addItem:"), items[i]);
        }

        // statusItemWithLength: NSVariableStatusItemLength (-1). The item must stay referenced or the icon disappears.
        var item = Send(Send(Cls("NSStatusBar"), Sel("systemStatusBar")), Sel("statusItemWithLength:"), -1.0);
        Send(item, Sel("retain"));
        var button = Send(item, Sel("button"));
        var image = Send(Cls("NSImage"), Sel("imageWithSystemSymbolName:accessibilityDescription:"), Str("iphone"), Str("PhoneBox"));
        if (image != IntPtr.Zero)
        {
            Send(image, Sel("setTemplate:"), (IntPtr)1); // follows the menu bar's light/dark tint
            Send(button, Sel("setImage:"), image);
        }
        else Send(button, Sel("setTitle:"), Str("PhoneBox"));
        Send(item, Sel("setMenu:"), menu);

        Send(app, Sel("finishLaunching"));
        var mode = Str("kCFRunLoopDefaultMode");
        var shown = new string?[entries.Length];
        for (var tick = 0; !hostStopped() && !_quitting; tick++)
        {
            var pool = objc_autoreleasePoolPush();
            try
            {
                if (tick % 2 == 0) Refresh(items, shown);
                var until = Send(Cls("NSDate"), Sel("dateWithTimeIntervalSinceNow:"), 0.5);
                var ev = SendEvent(app, Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"), ulong.MaxValue, until, mode, 1);
                if (ev != IntPtr.Zero) Send(app, Sel("sendEvent:"), ev);
            }
            finally { objc_autoreleasePoolPop(pool); }
        }
        Send(Send(Cls("NSStatusBar"), Sel("systemStatusBar")), Sel("removeStatusItem:"), item);
    }

    /// <summary>Applies the dynamic title, visibility, check mark and enabled state of every entry; only a changed title is set again.</summary>
    private static void Refresh(IntPtr[] items, string?[] shown)
    {
        for (var i = 0; i < _entries.Length; i++)
        {
            var entry = _entries[i];
            if (entry.IsSeparator) continue;
            try
            {
                if (entry.DynamicTitle is { } title && title() is var text && text != shown[i])
                {
                    shown[i] = text;
                    Send(items[i], Sel("setTitle:"), Str(text));
                }
                if (entry.Visible is { } visible) Send(items[i], Sel("setHidden:"), visible() ? IntPtr.Zero : (IntPtr)1);
                if (entry.Checked is { } check) Send(items[i], Sel("setState:"), check() ? (IntPtr)1 : IntPtr.Zero);
                if (entry.Enabled is { } enabled) Send(items[i], Sel("setEnabled:"), enabled() ? (IntPtr)1 : IntPtr.Zero);
            }
            catch (Exception ex) { Debug.WriteLine("menu bar refresh failed: " + ex.Message); }
        }
    }

    /// <summary>The menu: what the agent is doing, and what the user can ask of it.</summary>
    internal static MenuEntry[] Entries(AdbReverse adb, UpdateService updates, IHostApplicationLifetime life, string settingsUrl, string configDir)
    {
        static void Open(string target)
        {
            var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add(target);
            Process.Start(start);
        }
        var app = UpdateService.BundleDir(AppContext.BaseDirectory);
        var otherLoginItem = File.Exists(MacLoginItem.SetupScriptPlist);
        return
        [
            new("PhoneBox " + AppPaths.Version),
            new("Waiting for a phone…", DynamicTitle: () => adb.Connected ? "Phone connected" : "Waiting for a phone…"),
            MenuEntry.Separator,
            new("Open Settings", () => Open(settingsUrl), Key: ","),
            MenuEntry.Separator,
            new("Install update", () => _ = updates.ApplyAsync(), DynamicTitle: () => "Install update " + updates.Status().Latest,
                Visible: () => updates.Status() is { AgentUpdate: true } or { AppUpdate: true }),
            new("Check for Updates", () => _ = updates.CheckAsync()),
            new("Restart Agent", () => { AppRestart.Requested = true; life.StopApplication(); }),
            MenuEntry.Separator,
            new("Show Log", () => Open(AppPaths.LogPath)),
            new("Open Config Folder", () => Open(configDir)),
            new(otherLoginItem ? "Start at Login (set up by setup.sh)" : "Start at Login",
                app is null || otherLoginItem ? null : () => MacLoginItem.Set(app, !MacLoginItem.Enabled),
                Checked: () => MacLoginItem.Enabled || otherLoginItem, Enabled: () => app is not null && !otherLoginItem),
            MenuEntry.Separator,
            new("Quit PhoneBox", () => { _quitting = true; life.StopApplication(); }, Key: "q"),
        ];
    }

    private static void Guard(Action action)
    {
        try { action(); } catch (Exception ex) { Debug.WriteLine("menu bar action failed: " + ex.Message); }
    }
}

/// <summary>
/// "Start at Login" for PhoneBox.app: a launchd agent that opens the app when the user logs in. setup.sh's own agent
/// (net.oxog.phonebox.agent) is a different thing and takes precedence; running both would start two instances.
/// </summary>
internal static class MacLoginItem
{
    internal const string Label = "net.oxog.phonebox.app";
    internal static readonly string Plist = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");
    internal static readonly string SetupScriptPlist = Path.Combine(Path.GetDirectoryName(Plist)!, "net.oxog.phonebox.agent.plist");

    internal static bool Enabled => File.Exists(Plist);

    /// <summary>Takes effect at the next login; the app that is running now is left alone.</summary>
    internal static void Set(string appPath, bool on)
    {
        if (!on) { File.Delete(Plist); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Plist)!);
        File.WriteAllText(Plist, PlistText(appPath));
    }

    internal static string PlistText(string appPath) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>Label</key><string>{Label}</string>
          <key>ProgramArguments</key><array><string>/usr/bin/open</string><string>-a</string><string>{System.Security.SecurityElement.Escape(appPath)}</string></array>
          <key>RunAtLoad</key><true/>
        </dict></plist>
        """;
}
