package net.oxog.phonebox.deskpanel

import android.Manifest
import android.app.Activity
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.view.ViewGroup
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView

/**
 * The phone's "Phone bridge" screen: the clipboard switch, a short how-to, and an honest status line (whether the
 * service is connected, or the computer's switch is off), so a feature that is switched on never looks like it works
 * when it is not. Opens from the panel, the clipboard service's notification and a long press on the Send to Mac tile.
 */
class BridgeActivity : Activity() {
    private lateinit var status: TextView
    private lateinit var access: TextView
    private lateinit var last: TextView
    private val handler = Handler(Looper.getMainLooper())
    private val refresh = object : Runnable {
        override fun run() {
            status.text = statusText()
            access.text = getString(when {
                !notificationAccessAllowed() -> R.string.bridge_access_missing
                // allowed, switch on, but Android has not bound the listener (Samsung's battery management can unbind it)
                BridgePrefs.notificationsEnabled(this@BridgeActivity) && !PhoneNotificationListener.connected -> R.string.bridge_access_idle
                else -> R.string.bridge_access_granted
            })
            last.text = getString(when (BridgePrefs.notificationOutcome) {
                NotificationOutcome.NONE -> R.string.bridge_last_none
                NotificationOutcome.OK -> R.string.bridge_last_ok
                NotificationOutcome.MAC_OFF -> R.string.bridge_last_off
                NotificationOutcome.UNREACHABLE -> R.string.bridge_last_failed
            })
            handler.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (isOtherTilesLongPress(intent)) {
            // Android sends the long press on any of the app's tiles here once the filter exists: the PhoneBox tile's goes to
            // App info, which is what Android opened for it before the bridge (and still does while the bridge is off)
            try {
                startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", packageName, null))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            } catch (_: RuntimeException) {}
            finish()
            return
        }
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

        // ---- notifications on the Mac
        add(Switch(this).apply {
            text = getString(R.string.bridge_notifications_switch)
            textSize = 18f
            isChecked = BridgePrefs.notificationsEnabled(this@BridgeActivity)
            setOnCheckedChangeListener { _, on ->
                BridgePrefs.setNotificationsEnabled(this@BridgeActivity, on)
                BridgeEntryPoints.update(this@BridgeActivity) // the listener shows in the system's Notification access list only now
            }
        }, 5)
        add(TextView(this).apply { text = getString(R.string.bridge_notifications_help); textSize = 14f }, 1)
        access = TextView(this).apply { textSize = 16f }
        add(access, 2)
        add(Button(this).apply {
            text = getString(R.string.bridge_access_button)
            setOnClickListener { openNotificationAccess() }
        }, 1)
        add(TextView(this).apply { text = getString(R.string.bridge_restricted_help); textSize = 14f }, 1)
        add(Button(this).apply {
            text = getString(R.string.bridge_app_info_button)
            setOnClickListener {
                startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))
            }
        }, 1)
        add(Button(this).apply {
            text = getString(R.string.bridge_apps_button)
            setOnClickListener { startActivity(Intent(this@BridgeActivity, BridgeAppsActivity::class.java)) }
        }, 2)
        last = TextView(this).apply { textSize = 14f }
        add(last, 1)
        setContentView(ScrollView(this).apply { addView(column) })
    }

    override fun onResume() {
        super.onResume()
        // Android may have stopped the service (the Task Manager, an update): opening the screen brings it back
        if (BridgePrefs.clipboardEnabled(this) && BridgePrefs.clipboardState == ClipboardState.STOPPED) ClipboardService.sync(this)
        askForContactsOnce()
        handler.post(refresh)
    }

    /** With notifications on, Android's own question about Contacts, once: a ringing call then shows the contact's name on the Mac. */
    private fun askForContactsOnce() {
        if (Build.VERSION.SDK_INT < 23 || !BridgePrefs.notificationsEnabled(this) || ContactNames.allowed(this) || BridgePrefs.askedContacts(this)) return
        BridgePrefs.setAskedContacts(this)
        requestPermissions(arrayOf(Manifest.permission.READ_CONTACTS), 2)
    }

    override fun onPause() {
        handler.removeCallbacks(refresh)
        super.onPause()
    }

    /** A tile long press (QS_TILE_PREFERENCES names the tile in EXTRA_COMPONENT_NAME) for a tile other than Send to Mac. */
    private fun isOtherTilesLongPress(intent: Intent?): Boolean {
        if (intent?.action != "android.service.quicksettings.action.QS_TILE_PREFERENCES") return false
        // Intent.EXTRA_COMPONENT_NAME by value: public from Android 8, the tiles of Android 7 already send it under this name
        val key = "android.intent.extra.COMPONENT_NAME"
        val tile = try {
            if (Build.VERSION.SDK_INT >= 33) intent.getParcelableExtra(key, ComponentName::class.java)
            else @Suppress("DEPRECATION") intent.getParcelableExtra<ComponentName>(key)
        } catch (_: RuntimeException) { null } // exported: an extra that cannot be read counts as another tile
        return tile == null || tile.packageName != packageName || tile.className != "net.oxog.phonebox.deskpanel.SendClipboardTile"
    }

    // the setting is "pkg/Class:pkg/Class": compare whole package names, so another app whose name starts with ours does not count
    private fun notificationAccessAllowed(): Boolean =
        Settings.Secure.getString(contentResolver, "enabled_notification_listeners")?.split(':')
            ?.any { ComponentName.unflattenFromString(it)?.packageName == packageName } == true

    private fun openNotificationAccess() {
        try { startActivity(Intent("android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS")) }
        catch (_: Exception) { startActivity(Intent(Settings.ACTION_SETTINGS)) }
    }

    private fun statusText(): String {
        if (!BridgePrefs.clipboardEnabled(this)) return getString(R.string.bridge_state_off)
        return when (BridgePrefs.clipboardState) {
            ClipboardState.RUNNING -> getString(R.string.bridge_state_running)
            ClipboardState.MAC_OFF -> getString(R.string.bridge_state_mac_off)
            ClipboardState.UNSUPPORTED -> getString(R.string.bridge_state_unsupported)
            ClipboardState.WAITING -> getString(R.string.bridge_state_waiting)
            ClipboardState.PAIR_AGAIN -> getString(R.string.bridge_pair_again)
            ClipboardState.STOPPED -> getString(R.string.bridge_state_stopped) + (BridgePrefs.lastError?.let { " ($it)" } ?: "")
        }
    }
}
