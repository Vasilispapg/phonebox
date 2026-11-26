package net.oxog.phonebox.deskpanel

import android.content.Context

/**
 * The phone's own bridge switches ("Phone bridge" screen). A feature runs only when this switch and the one on the
 * computer (Settings › Phone bridge) are both on; the computer's switch is enforced by the agent.
 */
object BridgePrefs {
    private fun prefs(context: Context) = context.getSharedPreferences("bridge", Context.MODE_PRIVATE)

    fun clipboardEnabled(context: Context): Boolean = prefs(context).getBoolean("clipboard", false)

    fun setClipboardEnabled(context: Context, on: Boolean) {
        prefs(context).edit().putBoolean("clipboard", on).apply()
    }

    /** "Send files to the Mac": whether the Share sheet entry and the upload are on. The Mac's own switch is enforced by the agent. */
    fun filesFromPhoneEnabled(context: Context): Boolean = prefs(context).getBoolean("filesFromPhone", false)

    fun setFilesFromPhoneEnabled(context: Context, on: Boolean) {
        prefs(context).edit().putBoolean("filesFromPhone", on).apply()
    }

    /** "Receive files from the Mac": whether files the Mac offers are downloaded. The Mac's own switch is enforced by the agent. */
    fun filesToPhoneEnabled(context: Context): Boolean = prefs(context).getBoolean("filesToPhone", false)

    fun setFilesToPhoneEnabled(context: Context, on: Boolean) {
        prefs(context).edit().putBoolean("filesToPhone", on).apply()
    }

    /** The connection to the Mac's bridge stream is wanted while the clipboard or the files-from-the-Mac switch is on. */
    fun streamWanted(context: Context): Boolean = clipboardEnabled(context) || filesToPhoneEnabled(context)

    fun notificationsEnabled(context: Context): Boolean = prefs(context).getBoolean("notifications", false)

    /** Turning it on for the first time also lists the default dialer and SMS app (and nothing else) as the apps to show. */
    fun setNotificationsEnabled(context: Context, on: Boolean) {
        val p = prefs(context)
        val edit = p.edit().putBoolean("notifications", on)
        if (on && !p.contains("apps")) edit.putStringSet("apps", defaultApps(context))
        edit.apply()
        if (!on) {
            PhoneNotificationListener.clearPending() // what was waiting for the Mac must not be sent after the switch is off
            PhoneNotificationListener.forgetCalls()  // and no call is tracked while nothing is read
        }
    }

    /**
     * Show the song the phone itself plays in the panel's Now playing widget (and drive it from there) when the computer plays
     * nothing. Needs the notification listener, so it also keeps that listener enabled. Off until the owner turns it on.
     */
    fun phoneMediaEnabled(context: Context): Boolean = prefs(context).getBoolean("phoneMedia", false)

    fun setPhoneMediaEnabled(context: Context, on: Boolean) {
        prefs(context).edit().putBoolean("phoneMedia", on).apply()
        PhoneMediaMonitor.sync(context)
    }

    /** Whether the phone has asked for Contacts already: it asks once, and the owner can still allow it in Android's settings. */
    fun askedContacts(context: Context): Boolean = prefs(context).getBoolean("askedContacts", false)

    fun setAskedContacts(context: Context) {
        prefs(context).edit().putBoolean("askedContacts", true).apply()
    }

    /** "All apps" on the Phone bridge screen: every app's notifications go to the Mac, whatever the list says. Off until the owner turns it on. */
    fun allApps(context: Context): Boolean = prefs(context).getBoolean("allApps", false)

    fun setAllApps(context: Context, on: Boolean) {
        prefs(context).edit().putBoolean("allApps", on).apply()
    }

    /** The apps whose notifications go to the Mac. */
    fun allowedApps(context: Context): Set<String> = prefs(context).getStringSet("apps", emptySet()) ?: emptySet()

    fun setAllowedApps(context: Context, apps: Set<String>) {
        prefs(context).edit().putStringSet("apps", HashSet(apps)).apply()
    }

    /** The phone's own dialer and default messaging app: calls and texts are what people want on the Mac first. */
    fun defaultApps(context: Context): Set<String> {
        val apps = HashSet<String>()
        try {
            if (android.os.Build.VERSION.SDK_INT >= 23)
                (context.getSystemService(Context.TELECOM_SERVICE) as? android.telecom.TelecomManager)?.defaultDialerPackage?.let { apps.add(it) }
        } catch (_: Exception) {}
        try { android.provider.Telephony.Sms.getDefaultSmsPackage(context)?.let { apps.add(it) } } catch (_: Exception) {}
        return apps
    }

    /** How the last notification went, for the screen. In memory only. */
    @Volatile var notificationOutcome: NotificationOutcome = NotificationOutcome.NONE

    @Volatile private var state = ClipboardState.STOPPED

    /** Where the clipboard service is. A change is logged as "clipboard: A -> B" (states only, never any text), for diagnosing a stream that does not recover. */
    var clipboardState: ClipboardState
        get() = state
        set(value) {
            if (value != state) android.util.Log.i("PhoneBoxBridge", "clipboard: $state -> $value")
            state = value
        }

    /** The last thing that went wrong starting the service (a class name, never clipboard text), shown on the screen. */
    @Volatile var lastError: String? = null
}
