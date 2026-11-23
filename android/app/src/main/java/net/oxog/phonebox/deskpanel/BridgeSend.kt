package net.oxog.phonebox.deskpanel

import android.content.Context
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.widget.Toast

/**
 * Sends one text to the Mac's clipboard from the one-tap entry points (selection menu, Share, tile, notification button)
 * and says what happened in a toast. The text is held only for the request; nothing is stored or logged.
 */
object BridgeSend {
    fun send(context: Context, text: String?, sensitive: Boolean, done: () -> Unit = {}) {
        val app = context.applicationContext
        val main = Handler(Looper.getMainLooper())
        fun finish(message: Int) { main.post { Toast.makeText(app, message, Toast.LENGTH_SHORT).show(); done() } }
        if (!BridgePrefs.clipboardEnabled(app)) {
            // the phone's own switch is off: take the person to it instead of silently sending
            app.startActivity(Intent(app, BridgeActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            main.post { done() }
            return
        }
        when (BridgeClip.skipReason(text, sensitive)) {
            BridgeClip.Skip.NOTHING -> { finish(R.string.bridge_send_nothing); return }
            BridgeClip.Skip.TOO_LARGE -> { finish(R.string.bridge_send_too_large); return }
            null -> {}
        }
        Thread {
            finish(when (BridgeClient(AgentLink(app)).sendClipboard(text!!)) {
                SendResult.OK -> R.string.bridge_sent
                SendResult.OFF -> R.string.bridge_send_off
                SendResult.TOO_LARGE -> R.string.bridge_send_too_large
                SendResult.UNREACHABLE -> R.string.bridge_send_unreachable
            })
        }.start()
    }
}
