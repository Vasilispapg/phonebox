package net.oxog.phonebox.deskpanel

import android.app.Activity
import android.app.Dialog
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.Outline
import android.graphics.Typeface
import android.graphics.drawable.ColorDrawable
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.OpenableColumns
import android.text.TextUtils
import android.util.Size
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.ViewOutlineProvider
import android.view.Window
import android.widget.Button
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast

/**
 * "Send file to Mac" in the Share sheet (a file, a photo, several of them). See-through, with a confirmation that lists the first
 * names and sizes and a Send to Mac button. Any installed app can start this activity with any intent, so nothing is sent until the
 * owner has seen it and tapped Send, and only content URIs are taken, never one of this app's own providers (the pairing key lives
 * behind one). The upload itself runs in [FileUploadService], which outlives this window.
 */
class SendFilesToMacActivity : Activity() {
    private var dialog: Dialog? = null
    @Volatile private var gone = false

    /** What one line of the confirmation shows. [thumb] is null for a file the system cannot draw (the line shows its extension instead). */
    private class Row(val name: String, val size: Long?, val thumb: Bitmap?)

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
        // names, sizes and pictures come from other apps' providers, which can be slow: never on the main thread
        Thread {
            val rows = uris.take(SHOWN).map { describe(it) }
            runOnUiThread { if (!gone && !isFinishing) show(uris, rows) }
        }.start()
    }

    private fun show(uris: List<Uri>, rows: List<Row>) {
        val theme = BridgeTheme(this)
        val card = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(theme.dp(20), theme.dp(20), theme.dp(20), theme.dp(12))
            background = theme.roundRect(theme.card, 24, theme.cardStroke)
            // a tap that reaches a button through another app's window drawn on top does not count (tapjacking)
            filterTouchesWhenObscured = true
        }
        fun text(value: String, sp: Float, color: Int, bold: Boolean = false) = TextView(this).apply {
            this.text = value; textSize = sp; setTextColor(color)
            if (bold) typeface = Typeface.DEFAULT_BOLD
        }
        card.addView(text(getString(R.string.files_confirm_title), 21f, theme.text, bold = true))
        val list = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        for (row in rows) list.addView(fileRow(theme, row), LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = theme.dp(12) })
        card.addView(list)
        val more = uris.size - rows.size
        if (more > 0) card.addView(text(getString(R.string.files_more, more), 14f, theme.muted), LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = theme.dp(10) })

        val send = Button(this).apply {
            text = getString(R.string.bridge_send_to_mac); isAllCaps = false; textSize = 16f; typeface = Typeface.DEFAULT_BOLD
            setTextColor(Color.WHITE); minimumHeight = theme.dp(52); stateListAnimator = null
            background = theme.pressable(theme.roundRect(theme.accent, 16))
            setOnClickListener { FileUploadService.start(this@SendFilesToMacActivity, uris); finish() }
        }
        val cancel = Button(this).apply {
            text = getString(R.string.pair_negative); isAllCaps = false; textSize = 15f
            setTextColor(theme.accentText); minimumHeight = theme.dp(44); stateListAnimator = null
            background = theme.pressable(theme.roundRect(Color.TRANSPARENT, 14))
            setOnClickListener { finish() }
        }
        card.addView(send, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = theme.dp(18) })
        card.addView(cancel, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = theme.dp(4) })

        dialog = Dialog(this).apply {
            requestWindowFeature(Window.FEATURE_NO_TITLE)
            setContentView(card)
            window?.apply {
                setBackgroundDrawable(ColorDrawable(Color.TRANSPARENT))
                setLayout((resources.displayMetrics.widthPixels * 0.92f).toInt(), ViewGroup.LayoutParams.WRAP_CONTENT)
                setGravity(Gravity.CENTER)
                decorView.filterTouchesWhenObscured = true
            }
            setOnDismissListener { finish() } // Back or a tap outside: nothing is sent
            show()
        }
    }

    private fun fileRow(theme: BridgeTheme, row: Row): View {
        val box = theme.dp(52)
        val picture: View = if (row.thumb != null) ImageView(this).apply {
            setImageBitmap(row.thumb); scaleType = ImageView.ScaleType.CENTER_CROP
            outlineProvider = object : ViewOutlineProvider() {
                override fun getOutline(view: View, outline: Outline) { outline.setRoundRect(0, 0, view.width, view.height, theme.dp(12).toFloat()) }
            }
            clipToOutline = true
        } else TextView(this).apply {
            text = FileTransferRules.extensionLabel(row.name); gravity = Gravity.CENTER; textSize = 12f
            typeface = Typeface.DEFAULT_BOLD; setTextColor(theme.accentText)
            background = theme.roundRect(if (theme.dark) 0xFF26324A.toInt() else 0xFFE4ECFD.toInt(), 12)
        }
        val names = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(TextView(this@SendFilesToMacActivity).apply {
                text = row.name; textSize = 15f; typeface = Typeface.DEFAULT_BOLD; setTextColor(theme.text)
                maxLines = 1; ellipsize = TextUtils.TruncateAt.MIDDLE
            })
            if (row.size != null) addView(TextView(this@SendFilesToMacActivity).apply {
                text = FileTransferRules.formatSize(row.size); textSize = 13f; setTextColor(theme.muted)
            })
        }
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            addView(picture, LinearLayout.LayoutParams(box, box))
            addView(names, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { marginStart = theme.dp(12) })
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

    /** The name, size and (from Android 10, for pictures and videos) a small picture a provider gives; a provider that cannot say gives the last part of the URI. */
    private fun describe(uri: Uri): Row {
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
        val type = try { contentResolver.getType(uri) } catch (_: Exception) { null }
        var thumb: Bitmap? = null
        if (Build.VERSION.SDK_INT >= 29 && type != null && (type.startsWith("image/") || type.startsWith("video/"))) {
            try { thumb = contentResolver.loadThumbnail(uri, Size(160, 160), null) } catch (_: Exception) {}
        }
        return Row(FileTransferRules.displayName(name ?: uri.lastPathSegment), size, thumb)
    }

    override fun onDestroy() {
        gone = true
        dialog?.setOnDismissListener(null)
        dialog?.dismiss()
        dialog = null
        super.onDestroy()
    }

    private companion object {
        /** Enough for any photo burst; a longer list would be a hostile intent. */
        const val MAX_FILES = 50
        /** Lines the confirmation draws; the rest is counted ("+3 more"). */
        const val SHOWN = 5
    }
}
