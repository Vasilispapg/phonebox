package net.oxog.phonebox.deskpanel

import android.content.ClipData
import android.net.LocalServerSocket
import android.os.Build
import android.os.IBinder
import java.io.IOException
import java.lang.reflect.Method
import kotlin.system.exitProcess

/**
 * The automatic phone -> Mac clipboard. Android lets an app read the clipboard only while it is in front, but a process that
 * `adb shell` starts runs as the shell user, which the system treats as a system caller. The Mac's agent starts this class that
 * way (`app_process` with this APK on its class path), so it is not an app and has no permissions of its own; it only reads the
 * clipboard and hands each plain-text copy to the PhoneBox app with `content call` ([ClipProvider] answers it). It keeps running
 * when adb goes away, until the phone restarts or the app no longer wants it: the app's answer to every call says so.
 */
object ClipHelper {
    /** Bound by the helper only to be the one helper: the name can be bound once, and the agent looks for it to see if one runs. */
    const val CONTROL_SOCKET = "net.oxog.phonebox.cliphelper"
    private const val POLL_MS = 250L
    private const val PING_EVERY_POLLS = 60 // about every 15 seconds
    private const val CALL_WAIT_MS = 15_000L
    @Volatile private var control: LocalServerSocket? = null

    @JvmStatic
    fun main(args: Array<String>) {
        control = try { LocalServerSocket(CONTROL_SOCKET) } catch (_: IOException) { return }
        val reader = try { ClipboardReader() } catch (e: Exception) { System.err.println("cannot reach the clipboard: ${e.javaClass.simpleName}"); return }
        // a clipboard service that keeps failing (the system restarted it) ends the helper; the agent starts a fresh one
        val unreadable = ClipHelperRules.Streak(40)
        // an app that gives no answer at all (not installed, not running) twice in a row ends the helper; an answer of "off" ends it at once
        val unanswered = ClipHelperRules.Streak(2)
        fun answered(reply: ClipHelperRules.Reply) {
            if (reply == ClipHelperRules.Reply.OFF) exitProcess(0)
            if (unanswered.hit(reply == ClipHelperRules.Reply.NONE)) exitProcess(0)
        }
        val watcher = ClipWatcher({ reader.sample().also { if (unreadable.hit(it == null)) exitProcess(1) } }) { text -> answered(deliver(text)) }
        var polls = 0
        while (true) {
            watcher.poll()
            Thread.sleep(POLL_MS)
            if (++polls % PING_EVERY_POLLS != 0) continue
            // An update replaces the APK and an uninstall removes it: the running code is stale, and the agent starts the new one.
            if (ClipHelperRules.apkIsGone(System.getenv("CLASSPATH"))) exitProcess(0)
            answered(deliver(null))
        }
    }

    /** Calls the app's provider as the shell user with a copy (or a "still there?" for null) and reads its answer. */
    private fun deliver(text: String?): ClipHelperRules.Reply = try {
        val process = ProcessBuilder(ClipHelperRules.callCommand(text)).redirectErrorStream(true).start()
        process.outputStream.close()
        val output = StringBuilder()
        val readerThread = Thread { try { process.inputStream.bufferedReader(Charsets.UTF_8).forEachLine { output.append(it).append('\n') } } catch (_: IOException) {} }
        readerThread.isDaemon = true
        readerThread.start()
        readerThread.join(CALL_WAIT_MS)
        if (readerThread.isAlive) { process.destroy(); ClipHelperRules.Reply.NONE } else { process.waitFor(); ClipHelperRules.parseReply(output.toString()) }
    } catch (_: Exception) {
        ClipHelperRules.Reply.NONE
    }

    /** The system clipboard service, reached by reflection as the shell user. Never logs or prints what it reads. */
    private class ClipboardReader {
        private val service: Any
        private val get: Method

        init {
            val binder = Class.forName("android.os.ServiceManager").getMethod("getService", String::class.java).invoke(null, "clipboard") as IBinder
            service = Class.forName("android.content.IClipboard\$Stub").getMethod("asInterface", IBinder::class.java).invoke(null, binder)!!
            get = service.javaClass.methods.filter { it.name == "getPrimaryClip" }.maxByOrNull { it.parameterTypes.size }!!
        }

        fun sample(): ClipSample? = try {
            val clip = get.invoke(service, *ClipHelperRules.clipboardArgs(get.parameterTypes)) as ClipData?
            if (clip == null || clip.itemCount == 0) ClipSample(null, false)
            else ClipSample(clip.getItemAt(0).text?.toString(), isSensitive(clip))
        } catch (_: Exception) { // a refused call, a service that died: the caller counts these and gives up after a while
            null
        }

        // Android 13+ marks what a password manager copies; ClipDescription.getExtras exists from Android 7
        private fun isSensitive(clip: ClipData): Boolean =
            Build.VERSION.SDK_INT >= 24 && clip.description?.extras?.getBoolean("android.content.extra.IS_SENSITIVE", false) == true
    }
}
