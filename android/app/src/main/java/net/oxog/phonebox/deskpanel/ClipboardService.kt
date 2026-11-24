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
import android.content.SharedPreferences
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
    /** Which stream is the current one: what an old stream still delivers, or has queued for the main thread, is dropped. */
    @Volatile private var generation = 0

    /**
     * The stream's AgentLink read the paired computers once, when it was built. Choosing or forgetting a computer in the
     * panel (or a new pairing) changes them without the panel pausing, and the stream would go on sharing with the old
     * computer, a forgotten one included. Android calls this on the main thread, only for a value that really changed.
     * Held in a field: SharedPreferences keeps its listeners weakly. Posted, never run inside the change: building the new
     * AgentLink can itself save (moving an old pairing), and a restart nested in restartStream would leave two streams.
     */
    private val pairingChanged = SharedPreferences.OnSharedPreferenceChangeListener { _, _ ->
        main.post { if (stream != null && BridgePrefs.clipboardEnabled(this)) restartStream() }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        getSharedPreferences(AgentLink.PAIRING_PREFS, Context.MODE_PRIVATE).registerOnSharedPreferenceChangeListener(pairingChanged)
    }

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
        // the panel calls this on every resume: a connected stream is kept (cutting it would lose what is copied meanwhile and cost
        // a new TLS handshake each time); one that is not connected starts over at once instead of finishing a long back-off.
        // Pairing changes restart it through [pairingChanged].
        if (stream == null || BridgePrefs.clipboardState != ClipboardState.RUNNING) restartStream()
        return START_STICKY
    }

    /** A new link and stream each time, so they use the computers paired now. */
    private fun restartStream() {
        stream?.stop()
        val mine = ++generation
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        BridgePrefs.clipboardState = ClipboardState.WAITING
        // the bridge stream only pings every 15 s, so a read timeout of 40 s (the panel's 8 s would cut it and reconnect, losing
        // copies and the battery); failures back off from 2 s to a minute instead of hammering an absent Mac all day
        stream = AgentStream(
            AgentLink(applicationContext), "/api/bridge/stream", readTimeoutMs = 40_000,
            retryMinMs = 2_000, retryMaxMs = 60_000, refusedRetryMs = 60_000,
            onFailure = { code -> if (mine == generation) BridgePrefs.clipboardState = BridgeClip.stateAfterFailure(code) },
        ) { event, data ->
            if (mine != generation) return@AgentStream
            when (event) {
                "open" -> BridgePrefs.clipboardState = ClipboardState.RUNNING
                "close" -> BridgePrefs.clipboardState = ClipboardState.WAITING
                else -> BridgeClip.parseEvent(event, data)?.let { text ->
                    main.post { if (mine == generation && BridgePrefs.clipboardEnabled(this)) clipboard.setPrimaryClip(ClipData.newPlainText("PhoneBox", text)) }
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
        val send = PendingIntent.getActivity(this, 1, Intent(this, ReadClipboardActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), immutable or PendingIntent.FLAG_UPDATE_CURRENT)
        @Suppress("DEPRECATION") // the Icon-based Action builder is API 23, the app installs on 21
        builder.addAction(R.drawable.ic_tile, getString(R.string.bridge_send_to_mac), send)
        return builder.setSmallIcon(R.drawable.ic_tile)
            .setContentTitle(getString(R.string.bridge_notification_title))
            .setContentIntent(open)
            .setOngoing(true)
            .build()
    }

    override fun onDestroy() {
        getSharedPreferences(AgentLink.PAIRING_PREFS, Context.MODE_PRIVATE).unregisterOnSharedPreferenceChangeListener(pairingChanged)
        generation++
        stream?.stop()
        stream = null
        BridgePrefs.clipboardState = ClipboardState.STOPPED
        super.onDestroy()
    }

    companion object {
        private const val CHANNEL = "bridge"
        private const val NOTIFICATION_ID = 7

        /** Starts or stops the service to match the phone's switch. Safe to call often; a start reconnects a stream that is not connected. */
        fun sync(context: Context) {
            val app = context.applicationContext
            val intent = Intent(app, ClipboardService::class.java)
            // "Send to Mac" shows in other apps' menus only while the bridge is on
            BridgeEntryPoints.setEnabled(app, BridgePrefs.clipboardEnabled(app))
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
