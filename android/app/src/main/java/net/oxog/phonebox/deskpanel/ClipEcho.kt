package net.oxog.phonebox.deskpanel

import java.security.MessageDigest

/**
 * Remembers the texts the clipboard service itself put on the phone's clipboard (copies from the Mac). The helper sees them as
 * changes too, and sending them back would only make the Mac rewrite its own clipboard (and flatten whatever formatting it had).
 * An echo is recognised once, for [ttlMs], among the last four writes.
 */
class ClipEcho(private val ttlMs: Long = 5_000, private val clock: () -> Long) {
    private class Entry(val hash: String, val at: Long)

    private val writes = ArrayDeque<Entry>()

    @Synchronized
    fun wrote(text: String) {
        writes.addLast(Entry(hash(text), clock()))
        while (writes.size > 4) writes.removeFirst()
    }

    /** True once for a text just written by the service; false for a real copy. */
    @Synchronized
    fun consumeIfEcho(text: String): Boolean {
        val now = clock()
        writes.removeAll { now - it.at > ttlMs }
        val match = writes.firstOrNull { it.hash == hash(text) } ?: return false
        writes.remove(match)
        return true
    }

    private fun hash(text: String): String =
        MessageDigest.getInstance("SHA-256").digest(text.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
}
