package net.oxog.phonebox.deskpanel

enum class SendResult { OK, OFF, TOO_LARGE, UNREACHABLE }

/** How the last notification went, for the screen. */
enum class NotificationOutcome { NONE, OK, MAC_OFF, UNREACHABLE }

/** Where the clipboard service is, for the Phone bridge screen. Kept in memory: it describes this process only. */
enum class ClipboardState { STOPPED, WAITING, RUNNING, MAC_OFF, UNSUPPORTED }

/**
 * The phone bridge's pure helpers. No Android types, so tools/test-bridge-checks.py runs them on a plain JVM. The small
 * JSON here covers only what the agent sends and takes: flat objects of strings and numbers.
 */
object BridgeClip {
    /** The most text the agent takes (UTF-8 bytes); longer text is refused with 413, so it is not even sent. */
    const val MAX_BYTES = 64 * 1024

    /** Why a clip is not sent: there is nothing to send (empty, or private), or it is over the limit. */
    enum class Skip { NOTHING, TOO_LARGE }

    /** Null when the clip may be sent. A sensitive clip (a password manager's) counts as nothing, whatever its size. */
    fun skipReason(text: String?, sensitive: Boolean): Skip? = when {
        text.isNullOrBlank() || sensitive -> Skip.NOTHING
        text.toByteArray(Charsets.UTF_8).size > MAX_BYTES -> Skip.TOO_LARGE
        else -> null
    }

    /** Whether to send this clip: not empty, not marked sensitive (a password manager's), and within the limit. */
    fun shouldSend(text: String?, sensitive: Boolean): Boolean = skipReason(text, sensitive) == null

    /** The text of a bridge "clipboard" event (data is {"seq":n,"text":"..."}); null for any other event or a bad frame. */
    fun parseEvent(event: String, data: String): String? {
        if (event != "clipboard") return null
        val text = Json(data).parseObject()?.get("text") as? String
        return text?.takeIf { it.isNotEmpty() }
    }

    /** The body of POST /api/bridge/clipboard. */
    fun requestBody(text: String): String = buildString(text.length + 12) {
        append("{\"text\":\"")
        escapeInto(this, text)
        append("\"}")
    }

    /** The body of POST /api/bridge/notification; [time] is when the phone posted it (milliseconds). */
    fun notificationBody(key: String, app: String, pkg: String, title: String, text: String, category: String, time: Long): String =
        buildString(title.length + text.length + 120) {
            fun field(name: String, value: String, last: Boolean = false) {
                append('"').append(name).append("\":\"")
                escapeInto(this, value)
                append(if (last) "\"," else "\",")
            }
            append('{')
            field("key", key); field("app", app); field("package", pkg); field("title", title); field("text", text); field("category", category)
            append("\"time\":").append(time).append('}')
        }

    /** A JSON object as a map (strings, numbers as Double, booleans, null, nested maps and lists), or null when it is not one. */
    fun parseObject(json: String): Map<String, Any?>? = Json(json).parseObject()

    private fun escapeInto(out: StringBuilder, text: String) {
        for (c in text) when {
            c == '"' -> out.append("\\\"")
            c == '\\' -> out.append("\\\\")
            c == '\n' -> out.append("\\n")
            c == '\r' -> out.append("\\r")
            c == '\t' -> out.append("\\t")
            c < ' ' -> out.append("\\u").append(c.code.toString(16).padStart(4, '0'))
            else -> out.append(c)
        }
    }

    /**
     * What a failed attempt to open the stream tells the screen: 403 is the Mac's switch, 404 or 501 an agent that has no
     * bridge (an older one, or a Windows or Linux one), anything else (no route, 401, 5xx) just means waiting.
     */
    fun stateAfterFailure(code: Int?): ClipboardState = when (code) {
        403 -> ClipboardState.MAC_OFF
        404, 501 -> ClipboardState.UNSUPPORTED
        else -> ClipboardState.WAITING
    }

    /** What the agent's answer to a send means. */
    fun resultFor(code: Int): SendResult = when (code) {
        200 -> SendResult.OK
        403 -> SendResult.OFF
        413 -> SendResult.TOO_LARGE
        else -> SendResult.UNREACHABLE
    }

    /**
     * Whether the agent's /api/config says the computer shows phone notifications. The reply is {"version": "...", "config":
     * { ... "bridge": {...} }}. True or false; null when the reply is not that shape (then the answer is unknown, not "off").
     * An agent whose config has no bridge section (an older one) is off.
     */
    fun notificationsOn(replyJson: String): Boolean? {
        val reply = Json(replyJson).parseObject() ?: return null
        val config = reply["config"] as? Map<*, *> ?: return null
        val bridge = config["bridge"] as? Map<*, *> ?: return false
        return bridge["notifications"] == true
    }

    /** At most [max] characters: longer text is cut and ends with an ellipsis, never in the middle of an emoji. */
    fun cut(text: String, max: Int): String {
        if (text.length <= max) return text
        var keep = max - 1
        if (keep > 0 && text[keep - 1].isHighSurrogate()) keep--
        return text.substring(0, keep) + "…"
    }

    /** A strict reader for one JSON object (strings, numbers, booleans, null, and nested objects and arrays); any problem makes [parseObject] return null. */
    private const val MAX_DEPTH = 64

    private class Json(private val s: String) {
        private var i = 0

        private var depth = 0

        fun parseObject(): Map<String, Any?>? = try {
            ws()
            val out = obj()
            ws(); if (i != s.length) fail()
            out
        } catch (_: IllegalArgumentException) { null }

        private fun obj(): Map<String, Any?> {
            if (++depth > MAX_DEPTH) fail()
            expect('{'); ws()
            val out = HashMap<String, Any?>()
            if (peek() == '}') i++
            else while (true) {
                ws(); val key = string(); ws(); expect(':'); ws()
                out[key] = value(); ws()
                when (next()) { ',' -> continue; '}' -> break; else -> fail() }
            }
            depth--
            return out
        }

        private fun arr(): List<Any?> {
            if (++depth > MAX_DEPTH) fail()
            expect('['); ws()
            val out = ArrayList<Any?>()
            if (peek() == ']') i++
            else while (true) {
                ws(); out.add(value()); ws()
                when (next()) { ',' -> continue; ']' -> break; else -> fail() }
            }
            depth--
            return out
        }

        private fun fail(): Nothing = throw IllegalArgumentException()
        private fun peek(): Char = if (i < s.length) s[i] else fail()
        private fun next(): Char = peek().also { i++ }
        private fun expect(c: Char) { if (next() != c) fail() }
        private fun ws() { while (i < s.length && s[i] in " \t\r\n") i++ }

        private fun value(): Any? = when (peek()) {
            '{' -> obj()
            '[' -> arr()
            '"' -> string()
            't' -> literal("true", true)
            'f' -> literal("false", false)
            'n' -> literal("null", null)
            else -> number()
        }

        private fun literal(word: String, v: Any?): Any? {
            if (!s.startsWith(word, i)) fail()
            i += word.length
            return v
        }

        private fun number(): Double {
            val start = i
            while (i < s.length && s[i] in "+-.eE0123456789") i++
            return s.substring(start, i).toDoubleOrNull() ?: fail()
        }

        private fun string(): String {
            expect('"')
            val sb = StringBuilder()
            while (true) {
                val c = next()
                when {
                    c == '"' -> return sb.toString()
                    c < ' ' -> fail()
                    c != '\\' -> sb.append(c)
                    else -> when (val e = next()) {
                        '"', '\\', '/' -> sb.append(e)
                        'b' -> sb.append('\b')
                        'f' -> sb.append('\u000c')
                        'n' -> sb.append('\n')
                        'r' -> sb.append('\r')
                        't' -> sb.append('\t')
                        'u' -> {
                            if (i + 4 > s.length) fail()
                            sb.append(s.substring(i, i + 4).toIntOrNull(16)?.toChar() ?: fail())
                            i += 4
                        }
                        else -> fail()
                    }
                }
            }
        }
    }
}
