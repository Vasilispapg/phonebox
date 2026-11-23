package net.oxog.phonebox.deskpanel

import java.io.InputStream
import java.util.concurrent.CopyOnWriteArrayList

// Network boundary only: AgentStream.kt is compiled directly from production.
// [code] is the HTTP status of each attempt (200 unless a check wants a refusal), [offline] makes an attempt find no
// route at all (the production link answers null then); [paths] and [timeouts] record what was asked for.
class AgentLink(
    private val code: () -> Int = { 200 },
    private val offline: () -> Boolean = { false },
    private val next: () -> InputStream,
) {
    class Reply(val code: Int, val body: InputStream)
    enum class Route { USB, WIFI }
    val paths = CopyOnWriteArrayList<String>()
    val timeouts = CopyOnWriteArrayList<Int>()
    fun stream(path: String, readTimeoutMs: Int = STREAM_READ_TIMEOUT): Pair<Route, Reply>? {
        paths.add(path)
        timeouts.add(readTimeoutMs)
        if (offline()) return null
        return Route.USB to Reply(code(), next())
    }
    companion object { const val STREAM_READ_TIMEOUT = 8000 }
}
