package net.oxog.phonebox.deskpanel

/**
 * The rules behind showing the phone's own music in the panel's Now playing widget, kept free of Android types so they run
 * on a plain JVM (tools/test-bridge-checks.py). [PhoneMediaMonitor] feeds them from Android's media sessions.
 */

/** The facts about one active media session that decide which one the widget shows. */
data class SessionFacts(val pkg: String, val playing: Boolean, val paused: Boolean, val hasTitle: Boolean)

/** What the Now playing widget shows for the phone's song. The panel counts the position on by itself between updates. */
data class PhoneTrack(
    val pkg: String, val app: String, val title: String, val artist: String, val album: String,
    val playing: Boolean, val posMs: Long, val durMs: Long, val canSeek: Boolean, val rate: Double,
    val artId: Int,
)

object PhoneMediaModel {
    // android.media.session.PlaybackState values, copied so this file needs no Android classes
    const val STATE_PAUSED = 2
    const val STATE_PLAYING = 3
    const val STATE_BUFFERING = 6

    /** Ids of the phone's cover pictures start here, so they never equal the ids the computer's agent hands out. */
    const val ART_ID_BASE = 1_000_000

    /** Longest title, artist or album kept: a stream's "title" can be a whole paragraph. */
    private const val MAX_TEXT = 300

    fun isPlaying(state: Int) = state == STATE_PLAYING
    fun isPaused(state: Int) = state == STATE_PAUSED || state == STATE_BUFFERING

    /**
     * The index of the session to show, or -1. Android lists sessions with the most recently active first: the first one that
     * is playing wins, else the first one that is paused with a title (so its play button can resume it). PhoneBox's own
     * session (its Spotify-style controls elsewhere) and sessions without a title are never shown.
     */
    fun pick(own: String, sessions: List<SessionFacts>): Int {
        val usable = sessions.indices.filter { sessions[it].pkg != own && sessions[it].hasTitle }
        return usable.firstOrNull { sessions[it].playing } ?: usable.firstOrNull { sessions[it].paused } ?: -1
    }

    /**
     * Where the song is right now. A playback state is a position and the moment it was written (on the elapsed-realtime
     * clock); while playing, the time since then, at the playback speed, is added. Unknown positions (Android's -1) give 0.
     */
    fun position(positionMs: Long, updatedAtMs: Long, nowMs: Long, speed: Float, playing: Boolean, durMs: Long): Long {
        if (positionMs < 0) return 0
        val rate = rate(speed)
        val since = if (playing && updatedAtMs > 0 && nowMs > updatedAtMs) (nowMs - updatedAtMs) * rate else 0.0
        val pos = positionMs + since.toLong()
        return if (durMs > 0) minOf(durMs, maxOf(0L, pos)) else maxOf(0L, pos)
    }

    /** The track as it is [nowMs] - [atMs] after it was read: a playing song has moved on at its speed (never past its length). */
    fun advance(t: PhoneTrack?, atMs: Long, nowMs: Long): PhoneTrack? {
        if (t == null || !t.playing || nowMs <= atMs) return t
        val pos = t.posMs + ((nowMs - atMs) * rate(t.rate.toFloat())).toLong()
        return t.copy(posMs = if (t.durMs > 0) minOf(t.durMs, pos) else pos)
    }

    /** A session's playback speed as the song moves on at it: a speed of 0, below 0 or not a number counts as normal speed. */
    fun rate(speed: Float): Double = if (speed > 0f && speed.isFinite()) speed.toDouble() else 1.0

    /** Where a seek goes: not before the start, and not past the end when the length is known. */
    fun seekTarget(ms: Long, durMs: Long): Long = if (durMs > 0) ms.coerceIn(0L, durMs) else maxOf(0L, ms)

    /** A length of 0 or less means unknown (a live stream); the panel then shows no seek bar. */
    fun duration(durMs: Long): Long = if (durMs > 0) durMs else 0

    fun cut(text: String?): String {
        val t = text?.trim().orEmpty()
        return if (t.length <= MAX_TEXT) t else t.substring(0, MAX_TEXT)
    }

    /** The song as the panel's media object (the same fields the agent sends), or "null" for nothing playing. */
    fun json(t: PhoneTrack?): String {
        if (t == null) return "null"
        return StringBuilder(256).append("{\"phone\":true,\"app\":").append(quote(t.app))
            .append(",\"title\":").append(quote(t.title))
            .append(",\"artist\":").append(quote(t.artist))
            .append(",\"album\":").append(quote(t.album))
            .append(",\"playing\":").append(t.playing)
            .append(",\"posMs\":").append(t.posMs)
            .append(",\"durMs\":").append(t.durMs)
            .append(",\"artId\":").append(t.artId)
            .append(",\"canSeek\":").append(t.canSeek)
            .append(",\"playbackRate\":").append(rate(t.rate))
            .append(",\"ageMs\":0")
            .append('}').toString()
    }

    /**
     * Whether a new reading is worth telling the panel. Android repeats playback states (some apps write one every second);
     * the panel counts the position on by itself, so a reading that only says what it already counts is dropped. Anything else
     * (another song, play or pause, a new length, a seek of more than 1.5 s) goes through.
     */
    fun worthSending(prev: PhoneTrack?, prevAtMs: Long, next: PhoneTrack?, nowMs: Long): Boolean {
        if (prev == null || next == null) return prev != next
        if (prev.copy(posMs = 0) != next.copy(posMs = 0)) return true
        val counted = prev.posMs + if (prev.playing) ((nowMs - prevAtMs).coerceAtLeast(0) * prev.rate).toLong() else 0L
        return kotlin.math.abs(counted - next.posMs) > 1500
    }

    private fun rate(r: Double) = if (r > 0 && r.isFinite()) r.toString() else "1.0"

    /** A JSON string literal: quotes, backslashes and control characters escaped, so any title is safe to hand to the page. */
    fun quote(s: String): String {
        val sb = StringBuilder(s.length + 2).append('"')
        for (c in s) when {
            c == '"' -> sb.append("\\\"")
            c == '\\' -> sb.append("\\\\")
            c == '\n' -> sb.append("\\n")
            c == '\r' -> sb.append("\\r")
            c == '\t' -> sb.append("\\t")
            c < ' ' || c == '\u2028' || c == '\u2029' -> sb.append("\\u").append(String.format("%04x", c.code))
            else -> sb.append(c)
        }
        return sb.append('"').toString()
    }

    /** The phone's media volume as the widget's 0-100 (the system has a few steps, usually 15). */
    fun volumePercent(volume: Int, max: Int): Int = if (max <= 0) 0 else Math.round(volume.coerceIn(0, max) * 100f / max)

    /** The system step nearest to a 0-100 level. */
    fun volumeStep(percent: Int, max: Int): Int = if (max <= 0) 0 else Math.round(percent.coerceIn(0, 100) * max / 100f).coerceIn(0, max)

    /** The same fields the agent's audio state has, so the widget's volume row can show the phone's instead. */
    fun audioJson(volume: Int, max: Int, muted: Boolean): String =
        "{\"volume\":${volumePercent(volume, max)},\"muted\":$muted,\"canControl\":true,\"phone\":true}"

    /** The buttons the page may press; anything else is ignored. */
    fun validAction(action: String) = action == "playpause" || action == "next" || action == "prev" || action == "seek"
}
