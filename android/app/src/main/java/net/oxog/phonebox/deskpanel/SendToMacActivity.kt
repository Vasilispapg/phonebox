package net.oxog.phonebox.deskpanel

import android.app.Activity
import android.content.Intent
import android.os.Build
import android.os.Bundle

/**
 * "Send to Mac" in the text-selection menu (next to Copy, from Android 6) and in the Share sheet. See-through: it sends
 * the selected or shared text and closes at once.
 */
class SendToMacActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // exported, so any app can start it: extras that cannot be read (a Parcelable class this app does not have) must not
        // crash the process, which also runs the panel and the clipboard service. They count as nothing to send.
        val text = try {
            when (intent?.action) {
                Intent.ACTION_PROCESS_TEXT -> if (Build.VERSION.SDK_INT >= 23) intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString() else null
                Intent.ACTION_SEND -> intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString() // styled text is not a String
                else -> null
            }
        } catch (_: RuntimeException) { null }
        BridgeSend.send(this, text, sensitive = false) { finish() }
    }
}
