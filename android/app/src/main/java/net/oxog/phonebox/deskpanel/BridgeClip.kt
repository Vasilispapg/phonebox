package net.oxog.phonebox.deskpanel

/** [UNKNOWN]: the text was sent but no answer came back (the link's 504), so it may well be on the Mac already.
 *  [PAIR_AGAIN]: the Mac refused this phone's key (401): it was paired again or got a new key. */
enum class SendResult { OK, OFF, TOO_LARGE, UNREACHABLE, UNKNOWN, PAIR_AGAIN }

/** Where the clipboard service is, for the Phone bridge screen. Kept in memory: it describes this process only. */
enum class ClipboardState { STOPPED, WAITING, RUNNING, MAC_OFF, UNSUPPORTED, PAIR_AGAIN }

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
        for (c in text) when {
            c == '"' -> append("\\\"")
            c == '\\' -> append("\\\\")
            c == '\n' -> append("\\n")
            c == '\r' -> append("\\r")
            c == '\t' -> append("\\t")
            c < ' ' -> append("\\u").append(c.code.toString(16).padStart(4, '0'))
            else -> append(c)
        }
        append("\"}")
    }

    /**
     * What a failed attempt to open the stream tells the screen: 403 is the Mac's switch, 404 or 501 an agent that has no
     * bridge (an older one, or a Windows or Linux one), 401 a key the Mac no longer accepts (it was paired again, or got a
     * new key: waiting would never end), anything else (no route, 5xx) just means waiting.
     */
    fun stateAfterFailure(code: Int?): ClipboardState = when (code) {
        401 -> ClipboardState.PAIR_AGAIN
        403 -> ClipboardState.MAC_OFF
        404, 501 -> ClipboardState.UNSUPPORTED
        else -> ClipboardState.WAITING
    }

    /** What the agent's answer to a send means. */
    fun resultFor(code: Int): SendResult = when (code) {
        200 -> SendResult.OK
        401 -> SendResult.PAIR_AGAIN
        403 -> SendResult.OFF
        413 -> SendResult.TOO_LARGE
        // AgentLink's answer when the request went out and the reply was lost: the Mac may have the text already
        504 -> SendResult.UNKNOWN
        else -> SendResult.UNREACHABLE
    }

    /** How much of a shared text the confirmation shows. The whole text is sent; this only keeps the dialog small. */
    const val PREVIEW_CHARS = 600

    /**
     * The start of [text] for the confirmation before Send to Mac, never longer than [max] characters plus an ellipsis, and
     * never cut inside a surrogate pair (half an emoji would show as a box). Only for display: it is not sent or logged.
     */
    fun preview(text: String, max: Int = PREVIEW_CHARS): String {
        if (text.length <= max) return text
        var end = max
        if (end > 0 && Character.isHighSurrogate(text[end - 1]) && Character.isLowSurrogate(text[end])) end--
        return text.substring(0, end).trimEnd() + "…"
    }

    /** A strict reader for one flat JSON object; any problem makes [parseObject] return null. */
    private class Json(private val s: String) {
        private var i = 0

        fun parseObject(): Map<String, Any?>? = try {
            ws(); expect('{'); ws()
            val out = HashMap<String, Any?>()
            if (peek() == '}') i++
            else while (true) {
                ws(); val key = string(); ws(); expect(':'); ws()
                out[key] = value(); ws()
                when (next()) { ',' -> continue; '}' -> break; else -> fail() }
            }
            ws(); if (i != s.length) fail()
            out
        } catch (_: IllegalArgumentException) { null }

        private fun fail(): Nothing = throw IllegalArgumentException()
        private fun peek(): Char = if (i < s.length) s[i] else fail()
        private fun next(): Char = peek().also { i++ }
        private fun expect(c: Char) { if (next() != c) fail() }
        private fun ws() { while (i < s.length && s[i] in " \t\r\n") i++ }

        private fun value(): Any? = when (peek()) {
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
