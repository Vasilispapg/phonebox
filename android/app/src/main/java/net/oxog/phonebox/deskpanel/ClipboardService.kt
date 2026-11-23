package net.oxog.phonebox.deskpanel

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper

/**
 * Holds the connection to the computer's bridge stream while Phone bridge › clipboard is on, and puts each text copied
 * on the computer on this phone's clipboard. A foreground service, because Android stops ordinary background work.
 * The text goes straight from the stream to the clipboard: it is not kept, logged or shown here.
 */
class ClipboardService : Service() {
    private var stream: AgentStream? = null
    private val main = Handler(Looper.getMainLooper())

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // Android wants startForeground soon after startForegroundService, even when we are about to stop again
        try { goForeground() } catch (e: Exception) {
            BridgePrefs.lastError = e.javaClass.simpleName
            BridgePrefs.clipboardState = ClipboardState.STOPPED
            stopSelf()
            return START_NOT_STICKY
        }
        if (!BridgePrefs.clipboardEnabled(this)) {
            stopSelf()
            return START_NOT_STICKY
        }
        BridgePrefs.lastError = null
        restartStream()
        return START_STICKY
    }

    /** A new link and stream each time: the activity may have paired another computer since the last one was built. */
    private fun restartStream() {
        stream?.stop()
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        BridgePrefs.clipboardState = ClipboardState.WAITING
        stream = AgentStream(
            AgentLink(applicationContext), "/api/bridge/stream", refusedRetryMs = 60_000,
            onRefused = { BridgePrefs.clipboardState = ClipboardState.MAC_OFF },
        ) { event, data ->
            when (event) {
                "open" -> BridgePrefs.clipboardState = ClipboardState.RUNNING
                "close" -> BridgePrefs.clipboardState = ClipboardState.WAITING
                else -> BridgeClip.parseEvent(event, data)?.let { text ->
                    main.post { clipboard.setPrimaryClip(ClipData.newPlainText("PhoneBox", text)) }
                }
            }
        }.also { it.start() }
    }

    private fun goForeground() {
        val notification = notification()
        if (Build.VERSION.SDK_INT >= 34) startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        else startForeground(NOTIFICATION_ID, notification)
    }

    private fun notification(): Notification {
        val immutable = if (Build.VERSION.SDK_INT >= 23) PendingIntent.FLAG_IMMUTABLE else 0
        val open = PendingIntent.getActivity(this, 0, Intent(this, BridgeActivity::class.java), immutable or PendingIntent.FLAG_UPDATE_CURRENT)
        val builder = if (Build.VERSION.SDK_INT >= 26) {
            val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            manager.createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.bridge_notification_channel), NotificationManager.IMPORTANCE_LOW))
            Notification.Builder(this, CHANNEL)
        } else {
            @Suppress("DEPRECATION")
            Notification.Builder(this).setPriority(Notification.PRIORITY_MIN)
        }
        return builder.setSmallIcon(R.drawable.ic_tile)
            .setContentTitle(getString(R.string.bridge_notification_title))
            .setContentIntent(open)
            .setOngoing(true)
            .build()
    }

    override fun onDestroy() {
        stream?.stop()
        stream = null
        BridgePrefs.clipboardState = ClipboardState.STOPPED
        super.onDestroy()
    }

    companion object {
        private const val CHANNEL = "bridge"
        private const val NOTIFICATION_ID = 7

        /** Starts or stops the service to match the phone's switch. Safe to call often; a start restarts the stream. */
        fun sync(context: Context) {
            val app = context.applicationContext
            val intent = Intent(app, ClipboardService::class.java)
            if (!BridgePrefs.clipboardEnabled(app)) {
                app.stopService(intent)
                return
            }
            try {
                if (Build.VERSION.SDK_INT >= 26) app.startForegroundService(intent) else app.startService(intent)
            } catch (e: Exception) { // Android 12+ refuses a foreground start from the background
                BridgePrefs.lastError = e.javaClass.simpleName
            }
        }
    }
}
