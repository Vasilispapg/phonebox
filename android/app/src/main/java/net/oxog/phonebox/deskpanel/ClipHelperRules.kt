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
            (if (text == null) emptyList() else listOf("--arg", COPY_PREFIX + text))

    /**
     * Android's `content` tool refuses an argument value that starts with two dashes ("usage: adb shell content ...", tried on a Galaxy
     * S25 Ultra), which would lose a copy such as "--help"; a copy that starts with one dash goes through. Every copy therefore starts
     * with this marker, which [ClipProvider] takes off again.
     */
    const val COPY_PREFIX = "="

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

    /** The text a call hands on to the relay: only a "copy" with text, and only while the app wants copies; a ping hands on nothing. */
    fun copyToRelay(state: String, method: String, arg: String?): String? =
        if (state == "active" && method == "copy" && arg != null && arg.startsWith(COPY_PREFIX)) arg.substring(COPY_PREFIX.length) else null

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

    /**
     * Runs [command] (no shell) and returns what it printed, stdout and stderr together; null when it cannot start or has not
     * finished within [waitMs] (it is then killed). The output is read on a thread of its own because Process.waitFor with a
     * timeout is Java 8 / Android 8, and the helper runs on Android 5 too.
     */
    fun run(command: List<String>, waitMs: Long): String? = try {
        val process = ProcessBuilder(command).redirectErrorStream(true).start()
        process.outputStream.close()
        val output = StringBuilder()
        val reader = Thread { try { process.inputStream.bufferedReader(Charsets.UTF_8).forEachLine { output.append(it).append('\n') } } catch (_: java.io.IOException) {} }
        reader.isDaemon = true
        reader.start()
        reader.join(waitMs)
        if (reader.isAlive) { process.destroy(); null } else { process.waitFor(); output.toString() }
    } catch (_: Exception) {
        null
    }

    /**
     * The helper's loop without its Android parts, so when it leaves can be checked on a JVM. Each [step] is one look at the
     * clipboard (through [ClipWatcher]) and, every [pingEvery] looks, a "still there?". It returns null to go on, or the exit code
     * to leave with: 0 when the app says "off", gives no answer twice in a row or its APK is gone; 1 when the clipboard cannot be
     * read [unreadableLimit] times in a row (the system restarted; the agent starts a fresh helper).
     */
    class Loop(
        read: () -> ClipSample?,
        private val deliver: (String?) -> Reply,
        private val apkGone: () -> Boolean,
        private val pingEvery: Int = 60,
        unreadableLimit: Int = 40,
    ) {
        private val unreadable = Streak(unreadableLimit)
        private val unanswered = Streak(2)
        private var exitCode: Int? = null
        private var looks = 0
        private val watcher = ClipWatcher({ read().also { if (unreadable.hit(it == null)) exitCode = 1 } }) { text ->
            // Checked before every copy, not only at the ping: once the app is removed its provider's name is free, and another app
            // that took it would be handed copies until the next ping.
            if (apkGone()) exitCode = 0 else answered(deliver(text))
        }

        private fun answered(reply: Reply) {
            if (reply == Reply.OFF || unanswered.hit(reply == Reply.NONE)) exitCode = 0
        }

        fun step(): Int? {
            watcher.poll()
            exitCode?.let { return it }
            if (++looks % pingEvery != 0) return null
            // An update replaces the APK and an uninstall removes it: the running code is stale, and the agent starts the new one.
            if (apkGone()) return 0
            answered(deliver(null))
            return exitCode
        }
    }
}
