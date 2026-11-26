package net.oxog.phonebox.deskpanel

import android.content.ComponentName
import android.content.Context
import android.graphics.Bitmap
import android.media.AudioManager
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Base64
import java.io.ByteArrayOutputStream

/**
 * Reads what the phone itself is playing (Spotify, YouTube Music, a browser...) from Android's media sessions, so the panel's
 * Now playing widget can show it and its buttons can drive it when the computer plays nothing. Reading sessions needs the
 * PhoneBox notification listener to be allowed (Android's rule), which is why it runs inside [PhoneNotificationListener].
 * Nothing here leaves the phone: the song goes to the panel's page in this app, never to the computer. It is off until the
 * "Show the phone's music" switch is on, and the cover picture is only encoded for the song being shown.
 */
object PhoneMediaMonitor {
    private val main = Handler(Looper.getMainLooper())
    private class Held(val controller: MediaController, val callback: MediaController.Callback)

    // all of these change on the main thread only (onMain); the volatile ones are also read from the page's JavaScript thread
    private var context: Context? = null
    private var manager: MediaSessionManager? = null
    private var sessionsListener: MediaSessionManager.OnActiveSessionsChangedListener? = null
    private var held: List<Held> = emptyList()
    private class Reading(val track: PhoneTrack?, val at: Long)
    @Volatile private var reading = Reading(null, 0)
    private var artKey = ""
    private var artData: String? = null
    private var artSeq = 0
    @Volatile private var chosen: MediaController? = null
    @Volatile private var artId = 0

    /** Called on the main thread with the song as JSON ("null" for none) whenever the panel should update. */
    @Volatile var onChange: ((String) -> Unit)? = null

    /** The song now, as the panel's media object, or "null". */
    fun current(): String {
        val r = reading // once: the track and the moment it was read belong together
        return PhoneMediaModel.json(PhoneMediaModel.advance(r.track, r.at, SystemClock.elapsedRealtime())) // its position as of now, not as of the last event
    }

    /** The cover picture of song [id] as a data URL, or "" (it is gone or never existed). */
    fun art(id: Int): String = if (id != 0 && id == artId) artData.orEmpty() else ""

    private fun onMain(block: () -> Unit) { if (Looper.myLooper() == main.looper) block() else main.post(block) }

    /** Starts or stops to match the phone's switch and whether Android has bound the notification listener. Safe to call often. */
    fun sync(app: Context) {
        val wanted = BridgePrefs.phoneMediaEnabled(app) && PhoneNotificationListener.connected
        val component = ComponentName(app, PhoneNotificationListener::class.java)
        onMain { if (wanted) start(app.applicationContext, component) else stop() }
    }

    private fun start(app: Context, component: ComponentName) {
        if (context != null && sessionsListener != null) return // already running
        stop()
        try {
            val m = app.getSystemService(Context.MEDIA_SESSION_SERVICE) as? MediaSessionManager ?: return
            val listener = MediaSessionManager.OnActiveSessionsChangedListener { adopt(it.orEmpty()) }
            m.addOnActiveSessionsChangedListener(listener, component, main)
            context = app
            manager = m
            sessionsListener = listener
            adopt(m.getActiveSessions(component))
        } catch (_: SecurityException) {
            stop() // notification access is not allowed (yet)
        } catch (_: Exception) {
            stop()
        }
    }

    fun stop() = onMain {
        try { sessionsListener?.let { manager?.removeOnActiveSessionsChangedListener(it) } } catch (_: Exception) {}
        sessionsListener = null
        manager = null
        context = null
        release()
        chosen = null
        reading = Reading(null, 0)
        artKey = ""; artData = null; artId = 0
        publish("null")
    }

    private fun release() {
        for (h in held) try { h.controller.unregisterCallback(h.callback) } catch (_: Exception) {}
        held = emptyList()
    }

    private fun adopt(controllers: List<MediaController>) {
        release()
        held = controllers.map { c ->
            val callback = object : MediaController.Callback() {
                override fun onPlaybackStateChanged(state: PlaybackState?) = refresh()
                override fun onMetadataChanged(metadata: MediaMetadata?) = refresh()
                override fun onSessionDestroyed() = refresh()
            }
            try { c.registerCallback(callback, main) } catch (_: Exception) {}
            Held(c, callback)
        }
        refresh()
    }

    private fun titleOf(md: MediaMetadata?): String =
        PhoneMediaModel.cut(md?.getString(MediaMetadata.METADATA_KEY_TITLE) ?: md?.getString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE))

    private fun refresh() {
        val app = context ?: return
        val facts = held.map { h ->
            val state = try { h.controller.playbackState?.state ?: 0 } catch (_: Exception) { 0 }
            val title = try { titleOf(h.controller.metadata) } catch (_: Exception) { "" }
            SessionFacts(h.controller.packageName, PhoneMediaModel.isPlaying(state), PhoneMediaModel.isPaused(state), title.isNotEmpty())
        }
        val at = PhoneMediaModel.pick(app.packageName, facts)
        val controller = if (at < 0) null else held[at].controller
        chosen = controller
        val track = controller?.let { try { build(app, it) } catch (_: Exception) { null } }
        val now = SystemClock.elapsedRealtime()
        val before = reading
        if (!PhoneMediaModel.worthSending(before.track, before.at, track, now)) return
        reading = Reading(track, now)
        publish(PhoneMediaModel.json(track))
    }

    private fun publish(next: String) {
        onChange?.invoke(next)
    }

    private fun build(app: Context, c: MediaController): PhoneTrack {
        val md = c.metadata
        val st = c.playbackState
        val title = titleOf(md)
        val artist = PhoneMediaModel.cut(md?.getString(MediaMetadata.METADATA_KEY_ARTIST) ?: md?.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST)
            ?: md?.getString(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE))
        val album = PhoneMediaModel.cut(md?.getString(MediaMetadata.METADATA_KEY_ALBUM))
        val state = st?.state ?: 0
        val playing = PhoneMediaModel.isPlaying(state)
        val dur = PhoneMediaModel.duration(md?.getLong(MediaMetadata.METADATA_KEY_DURATION) ?: 0)
        val known = st != null && st.position >= 0
        val pos = PhoneMediaModel.position(st?.position ?: -1, st?.lastPositionUpdateTime ?: 0, SystemClock.elapsedRealtime(),
            st?.playbackSpeed ?: 1f, playing, dur)
        val canSeek = dur > 0 && known && (st!!.actions and PlaybackState.ACTION_SEEK_TO) != 0L
        return PhoneTrack(c.packageName, label(app, c.packageName), title, artist, album, playing, pos, dur, canSeek,
            PhoneMediaModel.rate(st?.playbackSpeed ?: 1f), coverFor(c.packageName, title, artist, album, md))
    }

    /** The id of the cover picture for this song (0 for none); the picture is encoded once per song, and again only if it arrives late. */
    private fun coverFor(pkg: String, title: String, artist: String, album: String, md: MediaMetadata?): Int {
        val key = "$pkg|$title|$artist|$album"
        if (key == artKey && (artData != null || md == null)) return artId
        val bitmap = try {
            md?.getBitmap(MediaMetadata.METADATA_KEY_ART) ?: md?.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART)
                ?: md?.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON)
        } catch (_: Exception) { null }
        if (key == artKey && bitmap == null) return artId
        artKey = key
        artData = bitmap?.let { encode(it) }
        artId = if (artData == null) 0 else PhoneMediaModel.ART_ID_BASE + ++artSeq
        return artId
    }

    /** A small JPEG (at most 256 px) as a data URL: the panel shows it in a card a few centimetres wide. */
    private fun encode(src: Bitmap): String? = try {
        val side = maxOf(src.width, src.height)
        val scaled = if (side > 256) Bitmap.createScaledBitmap(src, maxOf(1, src.width * 256 / side), maxOf(1, src.height * 256 / side), true) else src
        val out = ByteArrayOutputStream()
        scaled.compress(Bitmap.CompressFormat.JPEG, 80, out)
        "data:image/jpeg;base64," + Base64.encodeToString(out.toByteArray(), Base64.NO_WRAP)
    } catch (_: Exception) { null }

    private fun label(app: Context, pkg: String): String = try {
        app.packageManager.getApplicationLabel(app.packageManager.getApplicationInfo(pkg, 0)).toString()
    } catch (_: Exception) { pkg }

    private fun audio(app: Context) = app.getSystemService(Context.AUDIO_SERVICE) as AudioManager

    /** The phone's media volume and mute, in the shape of the agent's audio state: what the widget shows while the phone's song is shown. */
    fun audioJson(app: Context): String = try {
        val am = audio(app)
        val volume = am.getStreamVolume(AudioManager.STREAM_MUSIC)
        val muted = if (Build.VERSION.SDK_INT >= 23) am.isStreamMute(AudioManager.STREAM_MUSIC) else volume == 0
        PhoneMediaModel.audioJson(volume, am.getStreamMaxVolume(AudioManager.STREAM_MUSIC), muted)
    } catch (_: Exception) { "null" }

    /** Sets the phone's media volume to [percent] (0-100), without the system's volume panel. */
    fun setVolume(app: Context, percent: Int): Boolean = try {
        val am = audio(app)
        am.setStreamVolume(AudioManager.STREAM_MUSIC, PhoneMediaModel.volumeStep(percent, am.getStreamMaxVolume(AudioManager.STREAM_MUSIC)), 0)
        true
    } catch (_: Exception) { false }

    /** Mutes or unmutes the phone's media (Android 6 and later; older systems have no such call that works for an app). */
    fun toggleMute(app: Context): Boolean = try {
        if (Build.VERSION.SDK_INT < 23) false
        else { audio(app).adjustStreamVolume(AudioManager.STREAM_MUSIC, AudioManager.ADJUST_TOGGLE_MUTE, 0); true }
    } catch (_: Exception) { false }

    /** A button of the Now playing widget pressed while the phone's song is shown. False when nothing is there to control. */
    fun control(action: String, ms: Long): Boolean {
        if (!PhoneMediaModel.validAction(action)) return false
        val c = chosen ?: return false
        return try {
            val t = c.transportControls
            when (action) {
                "playpause" -> if (PhoneMediaModel.isPlaying(c.playbackState?.state ?: 0)) t.pause() else t.play()
                "next" -> t.skipToNext()
                "prev" -> t.skipToPrevious()
                "seek" -> t.seekTo(maxOf(0L, ms))
            }
            true
        } catch (_: Exception) { false }
    }
}
