package net.oxog.phonebox.deskpanel

/**
 * Notifications that could not reach the Mac, kept in memory for a short while: at most [max], none older than [ttlMs]
 * (a login code older than two minutes is no use). Nothing is written to disk.
 */
class DeliveryQueue<T>(private val max: Int = 20, private val ttlMs: Long = 120_000, private val clock: () -> Long) {
    class Entry<T>(val at: Long, val item: T)

    private val items = ArrayDeque<Entry<T>>()

    /** [at] is when the item first happened: a retry puts it back with its original time so it still expires on schedule. */
    @Synchronized
    fun add(item: T, at: Long = clock()) {
        items.addLast(Entry(at, item))
        while (items.size > max) items.removeFirst()
    }

    @Synchronized
    fun isEmpty(): Boolean = items.isEmpty()

    /** Forgets everything waiting (the switch was turned off). */
    @Synchronized
    fun clear() = items.clear()

    /** Whether an entry taken earlier is still inside its time limit (a slow send can make later ones of a batch too old). */
    fun isFresh(entry: Entry<T>): Boolean = clock() - entry.at < ttlMs

    /** Empties the queue and returns what is still fresh, oldest first. */
    @Synchronized
    fun take(): List<Entry<T>> {
        val now = clock()
        val fresh = items.filter { now - it.at < ttlMs }
        items.clear()
        return fresh
    }
}
