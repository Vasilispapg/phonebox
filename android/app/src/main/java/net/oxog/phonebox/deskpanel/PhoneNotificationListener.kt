package net.oxog.phonebox.deskpanel

import android.app.Notification
import android.content.ComponentName
import android.content.Context
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
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

    // Turning the switch off disables this component and Android unbinds it, not always through onListenerDisconnected (and
    // never below Android 7): without this the screen would say "allowed" after the switch comes back but before a rebind
    override fun onDestroy() {
        connected = false
        super.onDestroy()
    }

    override fun onListenerDisconnected() {
        connected = false
        // Android may drop the listener; ask to be bound again while the switch is on
        if (Build.VERSION.SDK_INT >= 24 && BridgePrefs.notificationsEnabled(this))
            requestRebind(ComponentName(this, PhoneNotificationListener::class.java))
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        val app = applicationContext
        val mark = queue.mark()
        if (!BridgePrefs.notificationsEnabled(app)) return
        val info = describe(sbn) ?: return
        val decision = filter.decide(info)
        if (decision == Decision.SKIP) return
        val incomingCall = decision == Decision.SEND && info.category == "call"
        // one call is sent once: answering, updating or ending it posts the same notification again
        if ((incomingCall || decision == Decision.SEND_AS_MISSED_CALL) && !callKeys.firstTime(info.key)) return
        val category = when { decision == Decision.SEND_AS_MISSED_CALL -> "missed_call"; incomingCall -> "call"; else -> "message" }
        // cut here, as the agent does: a long email in a BigText notification must arrive shortened, not be refused whole
        val body = BridgeClip.notificationBody(info.key, label(info.pkg), info.pkg, BridgeClip.cut(info.title, 200), BridgeClip.cut(info.text, 2000), category, sbn.postTime)
        if (queue.addIfCurrent(NotificationDelivery.Pending(info.pkg, body), mark)) requestDrain(app)
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
        val baseKey = sbn.key ?: "${sbn.packageName}|${sbn.id}|${sbn.tag}"
        // dialers post every call under one id and tag, so a call's key includes when it started: without that, only the first
        // call after the process started would ever be sent, here and on the agent (which de-duplicates by key and text)
        val key = if (n.category == "call" || n.category == "missed_call") "$baseKey|${n.`when`}" else baseKey
        return NotificationInfo(
            pkg = sbn.packageName, key = key,
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
        /** True while Android has this listener bound (notification access is allowed and the system started it). */
        @Volatile var connected = false

        private val worker = Executors.newSingleThreadExecutor()
        private val callKeys = CallKeys()
        private val queue = DeliveryQueue<NotificationDelivery.Pending>(clock = SystemClock::elapsedRealtime)
        private val draining = AtomicBoolean(false)
        private val retryPending = AtomicBoolean(false)
        private var cache: Pair<String?, BridgeConfigCache>? = null

        /** Forgets what waits for the Mac: called when the switch is turned off, so nothing is sent afterwards. */
        fun clearPending() = queue.clear()

        /**
         * Another computer was chosen or one was forgotten in the panel: what waited was meant for the old one, and the
         * cached state of its switch says nothing about the new one (a cached "off" would drop notifications for a minute).
         */
        fun changeComputer(change: () -> Boolean): Boolean = synchronized(queue) {
            if (!change()) return@synchronized false
            cache = null
            queue.clear()
            BridgePrefs.notificationOutcome = NotificationOutcome.NONE
            true
        }

        /**
         * One drain at a time on one thread: a burst of notifications is one pass over the queue, and the queue's two-minute
         * limit applies to everything in it, so a slow or absent Mac cannot build a backlog that is sent late.
         */
        private fun requestDrain(app: Context) {
            if (!draining.compareAndSet(false, true)) return // the running pass picks up the new item
            worker.execute {
                var again: Boolean
                do {
                    val retryLater = try { drainOnce(app) } catch (_: Exception) { false }
                    draining.set(false)
                    if (retryLater) scheduleRetry(app)
                    again = !retryLater && !queue.isEmpty() && draining.compareAndSet(false, true)
                } while (again)
            }
        }

        private fun drainOnce(app: Context): Boolean {
            if (queue.isEmpty()) return false
            val mark = queue.mark()
            val link = AgentLink(app)
            try {
                val target = link.notificationTarget()
                val client = BridgeClient(link)
                val config = synchronized(queue) {
                    if (!queue.isCurrent(mark)) return false
                    cache?.takeIf { it.first == target.identity }?.second
                        ?: BridgeConfigCache(SystemClock::elapsedRealtime).also { cache = target.identity to it }
                }
                return NotificationDelivery(
                    queue,
                    phoneSwitchOn = { BridgePrefs.notificationsEnabled(app) },
                    appAllowed = { BridgePrefs.allowedApps(app).contains(it) },
                    macSwitchOn = { config.notificationsOn { client.fetchConfig(target) } },
                    send = { body -> client.sendNotification(target, body) },
                    onOutcome = { BridgePrefs.notificationOutcome = it },
                    markMacOff = config::markOff,
                    markMacOn = config::markOn,
                ).drain(mark)
            } finally { link.close() }
        }

        private fun scheduleRetry(app: Context) {
            if (!retryPending.compareAndSet(false, true)) return
            Handler(Looper.getMainLooper()).postDelayed({
                retryPending.set(false)
                requestDrain(app)
            }, 30_000)
        }
    }
}
