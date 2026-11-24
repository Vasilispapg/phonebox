package net.oxog.phonebox.deskpanel

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import android.widget.Toast

/**
 * "Send file to Mac" in the Share sheet (a file, a photo, several of them). See-through, with a confirmation that lists the first
 * names and sizes and a Send to Mac button. Any installed app can start this activity with any intent, so nothing is sent until the
 * owner has seen it and tapped Send, and only content URIs are taken, never one of this app's own providers (the pairing key lives
 * behind one). The upload itself runs in [FileUploadService], which outlives this window.
 */
class SendFilesToMacActivity : Activity() {
    private var dialog: AlertDialog? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val uris = acceptedUris()
        if (uris.isEmpty()) {
            // a share that is only text (this entry also matches */*): the text entry handles it, when it is switched on
            val text = try { intent?.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString() } catch (_: RuntimeException) { null }
            if (!text.isNullOrEmpty()) {
                try {
                    startActivity(Intent(this, SendToMacActivity::class.java).setAction(Intent.ACTION_SEND).setType("text/plain")
                        .putExtra(Intent.EXTRA_TEXT, text))
                } catch (_: RuntimeException) { Toast.makeText(this, R.string.files_send_failed, Toast.LENGTH_SHORT).show() }
            } else Toast.makeText(this, R.string.files_send_failed, Toast.LENGTH_SHORT).show()
            finish()
            return
        }
        if (!BridgePrefs.filesFromPhoneEnabled(this)) {
            // the phone's own switch is off: take the person to it instead of silently sending
            startActivity(Intent(this, BridgeActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            finish()
            return
        }
        val names = ArrayList<String>()
        val sizes = ArrayList<Long?>()
        for (uri in uris) {
            val (name, size) = describe(uri)
            names += name
            sizes += size
        }
        val shown = FileTransferRules.confirmLines(names, sizes, 5)
        val message = (shown.lines + if (shown.more > 0) listOf(getString(R.string.files_more, shown.more)) else emptyList()).joinToString("\n")
        val night = resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK == Configuration.UI_MODE_NIGHT_YES
        val theme = if (night) android.R.style.Theme_Material_Dialog_Alert else android.R.style.Theme_Material_Light_Dialog_Alert
        dialog = AlertDialog.Builder(this, theme)
            .setTitle(getString(R.string.files_confirm_title))
            .setMessage(message)
            .setPositiveButton(getString(R.string.bridge_send_to_mac)) { _, _ -> FileUploadService.start(this, uris); finish() }
            .setNegativeButton(getString(R.string.pair_negative), null)
            .setOnDismissListener { finish() } // Cancel, Back or a tap outside: nothing is sent
            .show()
            .apply {
                listOf(AlertDialog.BUTTON_POSITIVE, AlertDialog.BUTTON_NEGATIVE).forEach { getButton(it)?.isAllCaps = false }
                // a tap that reaches the button through another app's window drawn on top does not count (tapjacking)
                window?.decorView?.filterTouchesWhenObscured = true
            }
    }

    /** The shared content URIs, in order, without this app's own providers. An extra this app cannot read counts as nothing. */
    @Suppress("DEPRECATION")
    private fun acceptedUris(): List<Uri> {
        val found = ArrayList<Uri>()
        try {
            when (intent?.action) {
                Intent.ACTION_SEND -> intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM)?.let { found += it }
                Intent.ACTION_SEND_MULTIPLE -> intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)?.let { found += it }
            }
        } catch (_: RuntimeException) { found.clear() } // a Parcelable class this app does not have
        val own = ownAuthorities()
        return found.filter { FileTransferRules.acceptableUri(it.scheme, it.authority, own) }.distinct().take(MAX_FILES)
    }

    private fun ownAuthorities(): Set<String> = try {
        val providers = packageManager.getPackageInfo(packageName, PackageManager.GET_PROVIDERS).providers.orEmpty()
        providers.flatMap { (it.authority ?: "").split(';') }.filter { it.isNotEmpty() }.toSet()
    } catch (_: Exception) { setOf("$packageName.usb", "$packageName.clip") }

    /** The display name and size a provider reports; a provider that cannot say gives the last part of the URI and no size. */
    private fun describe(uri: Uri): Pair<String, Long?> {
        var name: String? = null
        var size: Long? = null
        try {
            contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
                if (c.moveToFirst()) {
                    c.getColumnIndex(OpenableColumns.DISPLAY_NAME).takeIf { it >= 0 }?.let { name = c.getString(it) }
                    c.getColumnIndex(OpenableColumns.SIZE).takeIf { it >= 0 && !c.isNull(it) }?.let { size = c.getLong(it).takeIf { s -> s >= 0 } }
                }
            }
        } catch (_: Exception) {}
        return FileTransferRules.displayName(name ?: uri.lastPathSegment) to size
    }

    override fun onDestroy() {
        dialog?.setOnDismissListener(null)
        dialog?.dismiss()
        dialog = null
        super.onDestroy()
    }

    private companion object {
        /** Enough for any photo burst; a longer list would be a hostile intent. */
        const val MAX_FILES = 50
    }
}
