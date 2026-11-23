package net.oxog.phonebox.deskpanel

/** What the filter needs to know about one posted notification. Built from a StatusBarNotification by the listener. */
data class NotificationInfo(
    val pkg: String,
    val key: String,
    val ongoing: Boolean,
    val groupSummary: Boolean,
    val category: String?,
    /** A CallStyle notification of type "incoming" (Android 12+). */
    val callStyleIncoming: Boolean,
    /** The notification offers an action that answers the call (for dialers that do not use CallStyle). */
    val hasAnswerAction: Boolean,
    val title: String,
    val text: String,
    val time: Long,
)

enum class Decision { SKIP, SEND, SEND_AS_MISSED_CALL }

/**
 * Decides which notifications go to the Mac. Only apps on the owner's list are ever sent; PhoneBox's own, group summaries,
 * empty ones and ongoing ones (media, downloads, a call in progress) are not. A call is the exception to "ongoing": dialers
 * post an incoming call as ongoing, so an incoming one (CallStyle incoming, or an answer action) is sent.
 * The call rule is written from the documented Android model; how the owner's own dialer marks these is to be confirmed.
 */
class NotificationFilter(private val allowed: () -> Set<String>, private val ownPackage: String) {
    fun decide(n: NotificationInfo): Decision {
        if (n.pkg == ownPackage || n.groupSummary) return Decision.SKIP
        if (n.title.isBlank() && n.text.isBlank()) return Decision.SKIP
        if (n.pkg !in allowed()) return Decision.SKIP
        return when (n.category) {
            "missed_call" -> Decision.SEND_AS_MISSED_CALL
            "call" -> if (n.callStyleIncoming || n.hasAnswerAction) Decision.SEND else Decision.SKIP
            else -> if (n.ongoing) Decision.SKIP else Decision.SEND
        }
    }
}

/** Remembers which call notifications were already sent, so a call that is answered, updated or ended is not sent again. */
class CallKeys(private val max: Int = 50) {
    private val seen = LinkedHashSet<String>()

    @Synchronized
    fun firstTime(key: String): Boolean {
        if (!seen.add(key)) return false
        while (seen.size > max) seen.remove(seen.first())
        return true
    }
}
