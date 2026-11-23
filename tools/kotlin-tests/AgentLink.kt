package net.oxog.phonebox.deskpanel

import java.io.InputStream
import java.util.concurrent.CopyOnWriteArrayList

// Network boundary only: AgentStream.kt is compiled directly from production.
// [code] is the HTTP status of each attempt (200 unless a check wants a refusal); [paths] records what was asked for.
class AgentLink(private val code: () -> Int = { 200 }, private val next: () -> InputStream) {
    class Reply(val code: Int, val body: InputStream)
    enum class Route { USB, WIFI }
    val paths = CopyOnWriteArrayList<String>()
    fun stream(path: String): Pair<Route, Reply> {
        paths.add(path)
        return Route.USB to Reply(code(), next())
    }
}
