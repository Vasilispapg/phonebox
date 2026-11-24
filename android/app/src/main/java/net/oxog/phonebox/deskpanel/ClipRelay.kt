package net.oxog.phonebox.deskpanel

import java.util.concurrent.Executors

/**
 * Where the helper's copies arrive (see [ClipProvider]): [sink] is whoever wants the texts now (the clipboard service while it
 * runs). One worker sends them; while it is busy only the newest copy waits, and one that waited too long is dropped, so a Mac that
 * is away does not collect a queue that would overwrite newer copies when it returns. A copy the service itself just wrote (the
 * Mac's own copy coming back) is dropped on arrival.
 */
object ClipRelay {
    @Volatile var sink: ((String) -> Unit)? = null
    /** The Mac said its clipboard switch is off: the helper is told to leave when it next asks. */
    @Volatile var macOff = false
    private val worker = Executors.newSingleThreadExecutor { r -> Thread(r, "clip-relay-worker").apply { isDaemon = true } }
    // a clock that a change of the phone's time does not move (after a jump back an echo would otherwise stay "fresh" for hours)
    private val clock = { System.nanoTime() / 1_000_000 }
    private val latest = LatestCopy(maxAgeMs = 10_000, clock)
    // Ten seconds covers the helper's path on a slow phone: a "still there?" call that is running when the Mac's copy lands, then
    // the copy's own call; each starts a fresh `content` process (about a second on a fast phone, several on a slow one).
    private val echo = ClipEcho(ttlMs = 10_000, clock)

    /** The service is putting [text] (a copy from the Mac) on the phone's clipboard: the helper will report it, and it must not go back. */
    fun wroteFromMac(text: String) = echo.wrote(text)

    fun submit(text: String) {
        // Checked on arrival, not by the worker: an echo waiting behind a slow send could outlive its window, and as the newest copy
        // it would push out a real copy that was waiting.
        if (echo.consumeIfEcho(text)) return
        latest.put(text)
        worker.execute {
            // taken whether or not anyone wants it, so a copy that arrives as the service stops does not stay in memory
            val waiting = latest.take() ?: return@execute
            sink?.invoke(waiting)
        }
    }
}
