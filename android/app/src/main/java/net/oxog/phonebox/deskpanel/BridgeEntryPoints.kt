package net.oxog.phonebox.deskpanel

import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build

/**
 * "Send to Mac" appears in the text-selection menu, the Share sheet and the Quick Settings tile list only while the phone's
 * clipboard switch is on, and PhoneBox appears in the system's Notification access list only while the notification switch
 * is on: nobody else's phone is changed by a feature they never turned on. The panel's overview button and the service's
 * notification open the Phone bridge screen, which is where the switches are.
 */
object BridgeEntryPoints {
    fun update(context: Context) {
        val clipboard = enabledState(BridgePrefs.clipboardEnabled(context))
        val pm = context.packageManager
        pm.setComponentEnabledSetting(ComponentName(context, SendToMacActivity::class.java), clipboard, PackageManager.DONT_KILL_APP)
        // by name, and only from Android 7: the tile class extends TileService, which older systems do not have
        if (Build.VERSION.SDK_INT >= 24)
            pm.setComponentEnabledSetting(ComponentName(context.packageName, "net.oxog.phonebox.deskpanel.SendClipboardTile"), clipboard, PackageManager.DONT_KILL_APP)
        pm.setComponentEnabledSetting(ComponentName(context, PhoneNotificationListener::class.java),
            enabledState(BridgePrefs.notificationsEnabled(context)), PackageManager.DONT_KILL_APP)
    }

    private fun enabledState(on: Boolean) =
        if (on) PackageManager.COMPONENT_ENABLED_STATE_ENABLED else PackageManager.COMPONENT_ENABLED_STATE_DISABLED
}
