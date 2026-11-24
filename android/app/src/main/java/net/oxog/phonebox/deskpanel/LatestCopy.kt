package net.oxog.phonebox.deskpanel

/** Keeps only the newest copy, and none older than [maxAgeMs]. Thread-safe. */
class LatestCopy(private val maxAgeMs: Long, private val clock: () -> Long) {
    private var text: String? = null
    private var at = 0L

    @Synchronized fun put(newText: String) { text = newText; at = clock() }

    /** The waiting copy, once; null when there is none or it waited too long. */
    @Synchronized fun take(): String? {
        val t = text ?: return null
        text = null
        return if (clock() - at > maxAgeMs) null else t
    }
}
