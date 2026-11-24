package net.oxog.phonebox.deskpanel

import java.io.InputStream
import java.util.concurrent.CopyOnWriteArrayList

// Network boundary only: AgentStream.kt is compiled directly from production.
// [code] is the HTTP status of each attempt (200 unless a check wants a refusal), [offline] makes an attempt find no
// route at all (the production link answers null then), [route] is the route each attempt reports; [paths], [timeouts]
// and [headers] record what was asked for.
class AgentLink(
    private val code: () -> Int = { 200 },
    private val offline: () -> Boolean = { false },
    private val route: () -> Route = { Route.USB },
    private val next: () -> InputStream,
) {
    class Reply(val code: Int, val body: InputStream)
    enum class Route { USB, WIFI }
    val paths = CopyOnWriteArrayList<String>()
    val timeouts = CopyOnWriteArrayList<Int>()
    val headers = CopyOnWriteArrayList<Map<String, String>>()
    /** What [upload] and [request] were asked: the path, content type and declared length of each upload. */
    val uploads = CopyOnWriteArrayList<Triple<String, String, Long?>>()
    var uploadReply: () -> Reply = { Reply(200, java.io.ByteArrayInputStream(ByteArray(0))) }
    var requestReply: () -> Reply = { Reply(200, java.io.ByteArrayInputStream(ByteArray(0))) }
    fun upload(pathAndQuery: String, contentType: String, length: Long?, open: () -> InputStream, onProgress: (Long) -> Unit,
               cancelled: () -> Boolean): Reply {
        uploads.add(Triple(pathAndQuery, contentType, length))
        // the same loop as the production link's, written out so the stream check (which compiles no file rules) still builds
        open().use { input ->
            val buffer = ByteArray(64 * 1024)
            var total = 0L
            while (!cancelled()) {
                val n = input.read(buffer)
                if (n < 0) break
                total += n
                onProgress(total)
            }
        }
        return uploadReply()
    }
    fun request(method: String, pathAndQuery: String): Reply = requestReply()
    fun request(method: String, pathAndQuery: String, body: ByteArray?, contentType: String?): Reply = requestReply()

    fun stream(path: String, readTimeoutMs: Int = STREAM_READ_TIMEOUT, headers: Map<String, String> = emptyMap()): Pair<Route, Reply>? {
        paths.add(path)
        timeouts.add(readTimeoutMs)
        this.headers.add(headers)
        if (offline()) return null
        return route() to Reply(code(), next())
    }
    companion object { const val STREAM_READ_TIMEOUT = 8000 }
}
