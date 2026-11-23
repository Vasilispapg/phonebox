package net.oxog.phonebox.deskpanel

import android.app.Notification
import android.content.ComponentName
import android.content.Context
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Shows the phone's notifications on the Mac. Android keeps a notification listener alive itself, so nothing here needs a
 * foreground service: each notification is one short request. Only apps on the owner's list are sent ([NotificationFilter]),
 * nothing when the computer's switch is off, and what cannot be delivered waits in memory for two minutes ([DeliveryQueue]).
 * The text is never logged or stored.
 */
class PhoneNotificationListener : NotificationListenerService() {
    private val filter by lazy { NotificationFilter({ BridgePrefs.allowedApps(applicationContext) }, packageName) }

    override fun onListenerConnected() { connected = true }

    override fun onListenerDisconnected() {
        connected = false
        // Android may drop the listener; ask to be bound again while the switch is on
        if (Build.VERSION.SDK_INT >= 24 && BridgePrefs.notificationsEnabled(this))
            requestRebind(ComponentName(this, PhoneNotificationListener::class.java))
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        val app = applicationContext
        if (!BridgePrefs.notificationsEnabled(app)) return
        val info = describe(sbn) ?: return
        val decision = filter.decide(info)
        if (decision == Decision.SKIP) return
        val incomingCall = decision == Decision.SEND && info.category == "call"
        // one call is sent once: answering, updating or ending it posts the same key again
        if ((incomingCall || decision == Decision.SEND_AS_MISSED_CALL) && !callKeys.firstTime(info.key)) return
        val category = when { decision == Decision.SEND_AS_MISSED_CALL -> "missed_call"; incomingCall -> "call"; else -> "message" }
        val body = BridgeClip.notificationBody(info.key, label(info.pkg), info.pkg, info.title, info.text, category, sbn.postTime)
        worker.execute { deliver(app, body, System.currentTimeMillis()) }
    }

    private fun describe(sbn: StatusBarNotification): NotificationInfo? {
        val n = sbn.notification ?: return null
        val extras = n.extras ?: return null
        val title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString() ?: ""
        val text = (extras.getCharSequence(Notification.EXTRA_BIG_TEXT) ?: extras.getCharSequence(Notification.EXTRA_TEXT))?.toString() ?: ""
        // CallStyle (Android 12+) says "incoming" itself (type 1); for a dialer without it, an incoming call is the one that
        // brings its own full-screen screen with two or more buttons (answer, decline)
        val callStyleIncoming = extras.getInt("android.callType", 0) == 1
        val answerAction = n.fullScreenIntent != null && (n.actions?.size ?: 0) >= 2
        return NotificationInfo(
            pkg = sbn.packageName, key = sbn.key ?: "${sbn.packageName}|${sbn.id}|${sbn.tag}",
            ongoing = n.flags and Notification.FLAG_ONGOING_EVENT != 0,
            groupSummary = n.flags and Notification.FLAG_GROUP_SUMMARY != 0,
            category = n.category, callStyleIncoming = callStyleIncoming, hasAnswerAction = answerAction,
            title = title, text = text, time = sbn.postTime,
        )
    }

    private fun label(pkg: String): String = try {
        packageManager.getApplicationLabel(packageManager.getApplicationInfo(pkg, 0)).toString()
    } catch (_: Exception) { pkg }

    companion object {
        /** True while Android has this listener bound (notification access is allowed). */
        @Volatile var connected = false

        private val worker = Executors.newSingleThreadExecutor()
        private val callKeys = CallKeys()
        private val queue = DeliveryQueue<String>(clock = System::currentTimeMillis)
        private val retryPending = AtomicBoolean(false)
        @Volatile private var cache: BridgeConfigCache? = null

        private fun configCache(app: Context): BridgeConfigCache =
            cache ?: BridgeConfigCache({ withLink(app) { BridgeClient(it).fetchConfig() } }, System::currentTimeMillis).also { cache = it }

        private fun <T> withLink(app: Context, block: (AgentLink) -> T): T {
            val link = AgentLink(app)
            try { return block(link) } finally { link.close() }
        }

        /** On the worker thread: the older waiting ones first, then this one; none at all when the computer's switch is known to be off. */
        private fun deliver(app: Context, body: String, at: Long) {
            val config = configCache(app)
            if (!config.notificationsOn()) return
            withLink(app) { link ->
                val client = BridgeClient(link)
                var reachable = true
                for (waiting in queue.take()) {
                    if (reachable) reachable = send(client, config, waiting.item, waiting.at)
                    else queue.add(waiting.item, waiting.at)
                }
                if (reachable) send(client, config, body, at) else queue.add(body, at)
            }
            if (!queue.isEmpty()) scheduleRetry(app)
        }

        /** True unless the computer could not be reached (then the notification is back in the queue with its original time). */
        private fun send(client: BridgeClient, config: BridgeConfigCache, body: String, at: Long): Boolean = when (client.sendNotification(body)) {
            SendResult.OK -> { config.markOn(); BridgePrefs.notificationOutcome = NotificationOutcome.OK; true }
            SendResult.OFF -> { config.markOff(); BridgePrefs.notificationOutcome = NotificationOutcome.MAC_OFF; true } // dropped: nothing to retry
            SendResult.TOO_LARGE -> true // cannot happen (the agent cuts long text); dropped
            SendResult.UNREACHABLE -> { queue.add(body, at); BridgePrefs.notificationOutcome = NotificationOutcome.UNREACHABLE; false }
        }

        private fun scheduleRetry(app: Context) {
            if (!retryPending.compareAndSet(false, true)) return
            Handler(Looper.getMainLooper()).postDelayed({
                retryPending.set(false)
                worker.execute {
                    val config = configCache(app)
                    if (!config.notificationsOn()) return@execute
                    withLink(app) { link ->
                        val client = BridgeClient(link)
                        var reachable = true
                        for (waiting in queue.take()) {
                            if (reachable) reachable = send(client, config, waiting.item, waiting.at)
                            else queue.add(waiting.item, waiting.at)
                        }
                    }
                    if (!queue.isEmpty()) scheduleRetry(app)
                }
            }, 30_000)
        }
    }
}
