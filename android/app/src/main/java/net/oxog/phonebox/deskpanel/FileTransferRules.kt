package net.oxog.phonebox.deskpanel

/**
 * The pure rules of the file transfer, with no Android types so they run in the JVM checks (tools/kotlin-tests): which shared
 * URIs may be sent, what a name, a type and a size turn into, and how the agent's file events are read. Nothing here opens a
 * file, a network connection or a content provider.
 */
object FileTransferRules {
    /** The agent refuses a file over this (413). */
    const val MAX_FILE_BYTES = 2L shl 30

    /** Where a received file is saved on the phone (Android 10 and later: the shared Pictures, Movies and Download folders). */
    enum class Target { PICTURES, MOVIES, DOWNLOADS }

    /** A file the Mac is offering: [id] is the agent's 32-character hex id, [name] is already cut to a plain file name. */
    data class IncomingFile(val id: String, val name: String, val size: Long, val mime: String)

    private val ID = Regex("^[0-9a-f]{32}$")
    private const val MAX_NAME_BYTES = 200

    /**
     * Whether a shared stream URI may be sent: only content URIs, and never one of this app's own providers (an intent from
     * another app could otherwise name the pairing key). An authority with an at sign is refused too: Android reads a number in
     * front of it as another user's profile, so it could reach a provider the plain name check does not see.
     */
    fun acceptableUri(scheme: String?, authority: String?, ownAuthorities: Set<String>): Boolean {
        if (scheme == null || !scheme.equals("content", ignoreCase = true)) return false
        if (authority.isNullOrEmpty() || authority.contains('@')) return false
        return ownAuthorities.none { it.equals(authority, ignoreCase = true) }
    }

    /** A name the agent will take: the last path part, no control characters, at most 200 UTF-8 bytes with a short extension kept, never empty. */
    fun displayName(raw: String?): String {
        if (raw == null) return "file"
        val last = raw.substring(maxOf(raw.lastIndexOf('/'), raw.lastIndexOf('\\')) + 1)
        val clean = StringBuilder()
        var i = 0
        while (i < last.length) {
            val cp = last.codePointAt(i)
            i += Character.charCount(cp)
            if (!Character.isISOControl(cp) && Character.getType(cp) != Character.FORMAT.toInt()) clean.appendCodePoint(cp)
        }
        val name = clean.toString().trim()
        if (name.isEmpty()) return "file"
        return shorten(name)
    }

    private fun byteLength(s: String) = s.toByteArray(Charsets.UTF_8).size

    private fun shorten(name: String): String {
        if (byteLength(name) <= MAX_NAME_BYTES) return name
        val dot = name.lastIndexOf('.')
        val extension = if (dot > 0 && byteLength(name.substring(dot)) <= 20) name.substring(dot) else ""
        val stem = if (extension.isEmpty()) name else name.substring(0, dot)
        val room = MAX_NAME_BYTES - byteLength(extension)
        val out = StringBuilder()
        var used = 0
        var i = 0
        while (i < stem.length) {
            val cp = stem.codePointAt(i)
            val piece = String(Character.toChars(cp))
            val size = byteLength(piece)
            if (used + size > room) break
            out.append(piece)
            used += size
            i += Character.charCount(cp)
        }
        return if (out.isEmpty()) "file$extension" else out.toString() + extension
    }

    fun targetFor(mime: String?): Target {
        val type = mime?.lowercase() ?: return Target.DOWNLOADS
        return when {
            type.startsWith("image/") -> Target.PICTURES
            type.startsWith("video/") -> Target.MOVIES
            else -> Target.DOWNLOADS
        }
    }

    /** The upload request: the name percent-encoded as UTF-8, and the size when it is known (a content URI often gives none). */
    fun uploadPath(name: String, size: Long?): String {
        val sb = StringBuilder("/api/bridge/files/in?name=").append(encode(name))
        if (size != null && size >= 0) sb.append("&size=").append(size)
        return sb.toString()
    }

    fun downloadPath(id: String) = "/api/bridge/files/out/$id"

    fun donePath(id: String) = "/api/bridge/files/out/$id/done"

    /**
     * Copies [input] to [output] in 64 KiB chunks, telling [onProgress] the bytes so far after each one. Returns the byte count, or
     * null when [cancelled] turned true (it is asked before every chunk, so a cancel takes effect within one chunk). Nothing is closed.
     */
    fun copy(input: java.io.InputStream, output: java.io.OutputStream, onProgress: (Long) -> Unit, cancelled: () -> Boolean): Long? {
        val buffer = ByteArray(64 * 1024)
        var total = 0L
        while (true) {
            if (cancelled()) return null
            val n = input.read(buffer)
            if (n < 0) return total
            output.write(buffer, 0, n)
            total += n
            onProgress(total)
        }
    }

    /** The lines of the confirmation: up to [maxLines] of "name — size" (just the name when the size is unknown), and how many more there are. */
    data class ConfirmLines(val lines: List<String>, val more: Int)

    fun confirmLines(names: List<String>, sizes: List<Long?>, maxLines: Int): ConfirmLines {
        val shown = names.take(maxLines).mapIndexed { i, name ->
            val size = sizes.getOrNull(i)
            if (size == null) name else "$name — ${formatSize(size)}"
        }
        return ConfirmLines(shown, maxOf(0, names.size - maxLines))
    }

    /** Decimal units with one decimal ("1.2 MB"); units are not translated. */
    fun formatSize(bytes: Long): String {
        if (bytes < 1000) return "$bytes B"
        val units = arrayOf("KB", "MB", "GB", "TB")
        var value = bytes / 1000.0
        var unit = 0
        while (value >= 1000 && unit < units.size - 1) { value /= 1000; unit++ }
        return String.format(java.util.Locale.ROOT, "%.1f %s", value, units[unit])
    }

    private fun encode(text: String): String {
        val sb = StringBuilder()
        for (b in text.toByteArray(Charsets.UTF_8)) {
            val c = b.toInt() and 0xff
            if (c in 'a'.code..'z'.code || c in 'A'.code..'Z'.code || c in '0'.code..'9'.code || c == '-'.code || c == '_'.code || c == '.'.code || c == '~'.code)
                sb.append(c.toChar())
            else sb.append('%').append("0123456789ABCDEF"[c shr 4]).append("0123456789ABCDEF"[c and 15])
        }
        return sb.toString()
    }

    /** The "file" event of the bridge stream (data is {"seq":n,"text":"{...}"}); null for any other event or a frame that does not fit. */
    fun parseFileEvent(event: String, data: String): IncomingFile? {
        if (event != "file") return null
        val inner = BridgeClip.parseObject(data)?.get("text") as? String ?: return null
        return fromMap(BridgeClip.parseObject(inner))
    }

    internal fun fromMap(map: Map<String, Any?>?): IncomingFile? {
        map ?: return null
        val id = map["id"] as? String ?: return null
        if (!ID.matches(id)) return null
        val name = map["name"] as? String ?: return null
        val size = (map["size"] as? Double)?.takeIf { it >= 0 && it == Math.floor(it) }?.toLong() ?: return null
        val mime = (map["mime"] as? String)?.takeIf { it.isNotBlank() && it.length <= 100 } ?: "application/octet-stream"
        return IncomingFile(id, displayName(name), size, mime)
    }
}
