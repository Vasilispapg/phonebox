package net.oxog.phonebox.deskpanel

/**
 * Sends what is waiting in the [DeliveryQueue] to the Mac, one batch at a time, and decides what must never be sent:
 * nothing while the phone's switch is off or the Mac's is known to be off (what waits is dropped, not held for later),
 * nothing for an app the owner has since turned off, nothing older than the queue's two minutes, and nothing more after
 * the Mac answers 403. No Android types, so tools/test-bridge-checks.py runs it.
 */
class NotificationDelivery(
    private val queue: DeliveryQueue<Pending>,
    private val phoneSwitchOn: () -> Boolean,
    private val appAllowed: (String) -> Boolean,
    private val macSwitchOn: () -> Boolean,
    private val send: (String) -> SendResult,
    private val onOutcome: (NotificationOutcome) -> Unit,
    private val markMacOff: () -> Unit,
    private val markMacOn: () -> Unit,
) {
    /** A notification waiting to go: the posting app (checked again at send time) and the JSON body. */
    class Pending(val pkg: String, val body: String)

    /** Sends the fresh notifications in order. True when some are left to try again later (the Mac could not be reached). */
    fun drain(): Boolean {
        if (!phoneSwitchOn()) { queue.clear(); return false }
        if (!macSwitchOn()) { queue.clear(); onOutcome(NotificationOutcome.MAC_OFF); return false }
        val waiting = queue.take() // anything older than two minutes is not in it
        var i = 0
        while (i < waiting.size) {
            val entry = waiting[i]
            if (!appAllowed(entry.item.pkg)) { i++; continue }
            when (send(entry.item.body)) {
                SendResult.OK -> { markMacOn(); onOutcome(NotificationOutcome.OK); i++ }
                SendResult.OFF -> { markMacOff(); onOutcome(NotificationOutcome.MAC_OFF); queue.clear(); return false }
                SendResult.TOO_LARGE -> i++ // the agent will never take it; dropped
                SendResult.UNREACHABLE -> {
                    for (rest in waiting.drop(i)) queue.add(rest.item, rest.at) // back with the original times
                    onOutcome(NotificationOutcome.UNREACHABLE)
                    return true
                }
            }
        }
        return false
    }
}
