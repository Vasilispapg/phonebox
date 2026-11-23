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
        val text = when (intent?.action) {
            Intent.ACTION_PROCESS_TEXT -> if (Build.VERSION.SDK_INT >= 23) intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString() else null
            Intent.ACTION_SEND -> intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString() // styled text is not a String
            else -> null
        }
        BridgeSend.send(this, text, sensitive = false) { finish() }
    }
}
