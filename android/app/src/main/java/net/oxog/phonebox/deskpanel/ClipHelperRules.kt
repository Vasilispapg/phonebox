package net.oxog.phonebox.deskpanel

import java.io.File

/**
 * The decisions of the clipboard helper that need no Android classes, so they can be checked on a plain JVM
 * (tools/kotlin-tests/ClipRelayChecks.kt): what the helper sends and how it reads the answer, what to pass to the clipboard
 * service, when the helper gives up.
 */
object ClipHelperRules {
    const val SHELL_UID = 2000
    const val OWN_PACKAGE = "com.android.shell"

    /** The most text the bridge moves, in UTF-8 bytes. */
    const val MAX_BYTES = 64 * 1024

    const val PACKAGE = "net.oxog.phonebox.deskpanel"
    /** The provider the helper calls (ClipProvider); it takes calls only from the shell user. */
    const val AUTHORITY = "$PACKAGE.clip"

    /**
     * An app's own socket cannot be reached from the shell user (SELinux refuses it, tried on a Galaxy S25 Ultra), and a broadcast
     * keeps its extras in the system's broadcast history, which anyone holding DUMP or a bug report can read. A provider call keeps
     * nothing: `content call` (run as the shell user, without a shell around it, so the text is one argument whatever it holds)
     * hands the text to ClipProvider and prints only the provider's short answer. Without text it is a "still there?".
     */
    fun callCommand(text: String?): List<String> =
        listOf("content", "call", "--user", "0", "--uri", "content://$AUTHORITY", "--method", if (text == null) "ping" else "copy") +
            (if (text == null) emptyList() else listOf("--arg", text))

    enum class Reply { ACTIVE, OFF, NONE }

    /** `content call` prints `Result: Bundle[{state=active}]`; no answer (no app, no provider, an error) is [Reply.NONE]. */
    fun parseReply(output: String): Reply = when (Regex("""state=(active|off)""").findAll(output).lastOrNull()?.groupValues?.get(1)) {
        "active" -> Reply.ACTIVE
        "off" -> Reply.OFF
        else -> Reply.NONE
    }

    /** What the app answers: it wants copies only while the service runs, the phone's switch is on and the Mac has not said it is off. */
    fun stateFor(serviceRunning: Boolean, phoneSwitchOn: Boolean, macOff: Boolean): String =
        if (serviceRunning && phoneSwitchOn && !macOff) "active" else "off"

    /** Only the shell user may feed the Mac's clipboard; the provider checks the binder caller, which an app cannot fake. */
    fun callerAllowed(callingUid: Int): Boolean = callingUid == SHELL_UID

    /** A NUL cannot go in a command line (Java refuses it, older versions cut the text there); such a copy is not sent. */
    fun canTravel(text: String): Boolean = '\u0000' !in text

    /**
     * Arguments for IClipboard.getPrimaryClip, whose signature differs between Android versions and makers: (callingPackage,
     * attributionTag, userId, deviceId) on current Android, fewer before it, (callingPackage, userId, attributionTag) on some. The
     * first String is the shell's package, other Strings are null, every int is 0 (user 0, the default device).
     */
    fun clipboardArgs(types: Array<Class<*>>): Array<Any?> {
        var strings = 0
        return Array(types.size) { i ->
            when (types[i]) {
                String::class.java -> if (strings++ == 0) OWN_PACKAGE else null
                Int::class.javaPrimitiveType -> 0
                else -> null
            }
        }
    }

    /** The APK the helper runs from (the first entry of its CLASSPATH); null when the helper was not started that way. */
    fun apkPath(classpath: String?): String? = classpath?.split(':')?.firstOrNull { it.isNotBlank() }

    /** An update replaces the APK and an uninstall removes it: the running code is then stale and the agent starts a fresh one. */
    fun apkIsGone(classpath: String?, exists: (String) -> Boolean = { File(it).exists() }): Boolean {
        val path = apkPath(classpath) ?: return false
        return !exists(path)
    }

    /** Counts bad results in a row; [hit] says true once there are [limit] of them. A good result starts the count again. */
    class Streak(private val limit: Int) {
        private var bad = 0
        fun hit(isBad: Boolean): Boolean {
            bad = if (isBad) bad + 1 else 0
            return bad >= limit
        }
    }
}
