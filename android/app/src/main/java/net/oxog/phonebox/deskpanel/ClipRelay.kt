package net.oxog.phonebox.deskpanel

import java.util.concurrent.Executors

/**
 * Where the helper's copies arrive (see [ClipProvider]): [sink] is whoever wants the texts now (the clipboard service while it
 * runs). One worker sends them; while it is busy only the newest copy waits, and one that waited too long is dropped, so a Mac that
 * is away does not collect a queue that would overwrite newer copies when it returns.
 */
object ClipRelay {
    @Volatile var sink: ((String) -> Unit)? = null
    /** The Mac said its clipboard switch is off: the helper is told to leave when it next asks. */
    @Volatile var macOff = false
    private val worker = Executors.newSingleThreadExecutor { r -> Thread(r, "clip-relay-worker").apply { isDaemon = true } }
    private val latest = LatestCopy(maxAgeMs = 10_000) { System.currentTimeMillis() }

    fun submit(text: String) {
        latest.put(text)
        worker.execute {
            val target = sink ?: return@execute
            latest.take()?.let(target)
        }
    }
}
