package net.oxog.phonebox.deskpanel

import android.Manifest
import android.app.Activity
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView

/**
 * The phone's "Phone bridge" screen: the clipboard switch, a short how-to, and an honest status line (whether the
 * service is connected, or the computer's switch is off), so a feature that is switched on never looks like it works
 * when it is not. Opens from the clipboard service's notification and from a long press on a PhoneBox tile.
 */
class BridgeActivity : Activity() {
    private lateinit var status: TextView
    private val handler = Handler(Looper.getMainLooper())
    private val refresh = object : Runnable {
        override fun run() {
            status.text = statusText()
            handler.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val pad = (16 * resources.displayMetrics.density).toInt()
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad, pad, pad)
        }
        fun add(view: android.view.View, top: Int = 0) = column.addView(view, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = top * pad / 2 })
        add(TextView(this).apply { text = getString(R.string.bridge_title); textSize = 24f })
        add(Switch(this).apply {
            text = getString(R.string.bridge_clipboard_switch)
            textSize = 18f
            isChecked = BridgePrefs.clipboardEnabled(this@BridgeActivity)
            setOnCheckedChangeListener { _, on ->
                BridgePrefs.setClipboardEnabled(this@BridgeActivity, on)
                if (on && Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
                    requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
                ClipboardService.sync(this@BridgeActivity)
            }
        }, 2)
        status = TextView(this).apply { textSize = 16f }
        add(status, 1)
        add(TextView(this).apply { text = getString(R.string.bridge_clipboard_help); textSize = 14f }, 2)
        setContentView(ScrollView(this).apply { addView(column) })
    }

    override fun onResume() {
        super.onResume()
        // Android may have stopped the service (the Task Manager, an update): opening the screen brings it back
        if (BridgePrefs.clipboardEnabled(this) && BridgePrefs.clipboardState == ClipboardState.STOPPED) ClipboardService.sync(this)
        handler.post(refresh)
    }

    override fun onPause() {
        handler.removeCallbacks(refresh)
        super.onPause()
    }

    private fun statusText(): String {
        if (!BridgePrefs.clipboardEnabled(this)) return getString(R.string.bridge_state_off)
        return when (BridgePrefs.clipboardState) {
            ClipboardState.RUNNING -> getString(R.string.bridge_state_running)
            ClipboardState.MAC_OFF -> getString(R.string.bridge_state_mac_off)
            ClipboardState.UNSUPPORTED -> getString(R.string.bridge_state_unsupported)
            ClipboardState.WAITING -> getString(R.string.bridge_state_waiting)
            ClipboardState.STOPPED -> getString(R.string.bridge_state_stopped) + (BridgePrefs.lastError?.let { " ($it)" } ?: "")
        }
    }
}
