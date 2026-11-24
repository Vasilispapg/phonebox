package net.oxog.phonebox.deskpanel

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.res.Configuration
import android.os.Build
import android.os.Bundle

/**
 * "Send to Mac" in the text-selection menu (next to Copy, from Android 6) and in the Share sheet. See-through, with a
 * small confirmation: the start of the text and a Send to Mac button. Any installed app can start this activity with any
 * text (the chooser starts it on the sender's behalf, so a caller check cannot tell), and that text would land on the
 * Mac's clipboard, ready to be pasted into a terminal. So nothing is sent until the owner has seen it and tapped Send.
 */
class SendToMacActivity : Activity() {
    private var dialog: AlertDialog? = null

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
        // the switch is off, or the text is empty or too long: say so without asking first (nothing is sent either way)
        if (!BridgeSend.sendable(this, text, sensitive = false) { finish() }) return
        val clip = text!!
        val night = resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK == Configuration.UI_MODE_NIGHT_YES
        val theme = if (night) android.R.style.Theme_Material_Dialog_Alert else android.R.style.Theme_Material_Light_Dialog_Alert
        dialog = AlertDialog.Builder(this, theme)
            .setTitle(getString(R.string.bridge_confirm_title))
            .setMessage(BridgeClip.preview(clip))
            .setPositiveButton(getString(R.string.bridge_send_to_mac)) { _, _ -> BridgeSend.send(this, clip, sensitive = false) { finish() } }
            .setNegativeButton(getString(R.string.pair_negative), null)
            .setOnDismissListener { finish() } // Cancel, Back or a tap outside: nothing is sent
            .show()
            .apply {
                // as the pairing dialog: the default all-caps follows the phone's language rules, not the text as written
                listOf(AlertDialog.BUTTON_POSITIVE, AlertDialog.BUTTON_NEGATIVE).forEach { getButton(it)?.isAllCaps = false }
                // a tap that reaches the button through another app's window drawn on top does not count (tapjacking)
                window?.decorView?.filterTouchesWhenObscured = true
            }
    }

    override fun onDestroy() {
        // a configuration change recreates the activity, and the dialog with it, from the same intent; this one must not leak
        dialog?.setOnDismissListener(null)
        dialog?.dismiss()
        dialog = null
        super.onDestroy()
    }
}
