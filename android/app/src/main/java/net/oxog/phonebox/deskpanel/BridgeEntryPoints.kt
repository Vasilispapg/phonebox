package net.oxog.phonebox.deskpanel

import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build

/**
 * "Send to Mac" appears in the text-selection menu, the Share sheet and the Quick Settings tile list, and a long press on a
 * PhoneBox tile comes to the app, only while the phone's clipboard switch is on; PhoneBox appears in the system's
 * Notification access list only while the notification switch is on; and the app starts at boot only while either one is on:
 * for everyone else the phone behaves as before. All of these are android:enabled="false" in the manifest. The panel's
 * overview button and the service's notification open the Phone bridge screen, which is where the switches are.
 */
object BridgeEntryPoints {
    /** The alias with the tile long-press filter (AndroidManifest.xml); an alias has no class to name. */
    const val TILE_PREFERENCES = "net.oxog.phonebox.deskpanel.BridgeTilePreferences"

    /** Sets every entry point from the two switches (each is left alone when it is already in the wanted state). */
    fun update(context: Context) {
        val clipboard = BridgePrefs.clipboardEnabled(context)
        val notifications = BridgePrefs.notificationsEnabled(context)
        val names = mutableListOf(SendToMacActivity::class.java.name, TILE_PREFERENCES)
        // by name, and only from Android 7: the tile class extends TileService, which older systems do not have
        if (Build.VERSION.SDK_INT >= 24) names += "net.oxog.phonebox.deskpanel.SendClipboardTile"
        apply(context, names, clipboard)
        apply(context, listOf(PhoneNotificationListener::class.java.name), notifications)
        apply(context, listOf(BridgeBootReceiver::class.java.name), clipboard || notifications)
    }

    /**
     * Safe to call on every resume: a component already in the wanted state is not touched (each change is written to disk).
     * Off goes back to the manifest's default (disabled); a component that was never switched on is left as it is.
     */
    private fun apply(context: Context, names: List<String>, on: Boolean) {
        val pm = context.packageManager
        for (name in names) {
            val component = ComponentName(context.packageName, name)
            val current = pm.getComponentEnabledSetting(component)
            if (on && current != PackageManager.COMPONENT_ENABLED_STATE_ENABLED)
                pm.setComponentEnabledSetting(component, PackageManager.COMPONENT_ENABLED_STATE_ENABLED, PackageManager.DONT_KILL_APP)
            else if (!on && current == PackageManager.COMPONENT_ENABLED_STATE_ENABLED)
                pm.setComponentEnabledSetting(component, PackageManager.COMPONENT_ENABLED_STATE_DEFAULT, PackageManager.DONT_KILL_APP)
        }
    }
}
