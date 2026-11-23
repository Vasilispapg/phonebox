package net.oxog.phonebox.deskpanel

import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build

/**
 * "Send to Mac" appears in the text-selection menu, the Share sheet and the Quick Settings tile list only while the
 * phone's Phone bridge switch is on: everyone else's apps keep their menus as they were. The panel's overview button and
 * the service's notification open the Phone bridge screen, which is where the switch is.
 */
object BridgeEntryPoints {
    fun setEnabled(context: Context, on: Boolean) {
        val pm = context.packageManager
        val state = if (on) PackageManager.COMPONENT_ENABLED_STATE_ENABLED else PackageManager.COMPONENT_ENABLED_STATE_DISABLED
        pm.setComponentEnabledSetting(ComponentName(context, SendToMacActivity::class.java), state, PackageManager.DONT_KILL_APP)
        // by name, and only from Android 7: the tile class extends TileService, which older systems do not have
        if (Build.VERSION.SDK_INT >= 24)
            pm.setComponentEnabledSetting(ComponentName(context.packageName, "net.oxog.phonebox.deskpanel.SendClipboardTile"), state, PackageManager.DONT_KILL_APP)
    }
}
