package net.oxog.phonebox.deskpanel

import android.app.Activity
import android.content.ClipboardManager
import android.content.Context
import android.os.Build
import android.os.Bundle

/**
 * Sends what is on the phone's clipboard. Android lets only the app in front read the clipboard, so this see-through
 * activity waits until it has window focus, reads once, sends, and closes. The tile and the notification button open it.
 */
class ReadClipboardActivity : Activity() {
    private var handled = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || handled) return
        handled = true
        val clip = (getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).primaryClip
        val text = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(this)?.toString()
        // Android 13+ marks what a password manager copies as sensitive; such a clip is never sent
        val sensitive = Build.VERSION.SDK_INT >= 33 && clip?.description?.extras?.getBoolean("android.content.extra.IS_SENSITIVE") == true
        BridgeSend.send(this, text, sensitive) { finish() }
    }
}
