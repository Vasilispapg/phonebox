package net.oxog.phonebox.deskpanel

import java.security.MessageDigest

/** What the clipboard holds at one moment: its text (null for an image, a file or nothing) and whether it is marked sensitive. */
class ClipSample(val text: String?, val sensitive: Boolean, val unknown: Boolean = false)

/**
 * Decides which clipboard changes the helper reports. The first look only notes what is there, so turning the feature on never
 * sends an old copy. After that a change is reported when it is plain text that is not blank, not marked sensitive (a password
 * manager's) and at most 64 KiB. A non-text copy resets it, so the same words copied again later count as a new copy; a read that
 * fails changes nothing. The text is held only as a hash.
 */
class ClipWatcher(private val read: () -> ClipSample?, private val emit: (String) -> Unit) {
    private var primed = false
    private var last: String? = null

    fun poll() {
        val sample = read() ?: return
        // the clipboard service answered nothing at all (Android 16 does that while the phone is locked): not a change, not even to "empty",
        // so what was copied before stays known and is not sent again after unlocking
        if (sample.unknown) return
        val text = sample.text
        if (text == null) { last = null; primed = true; return }
        val hash = hash(text)
        if (!primed) { primed = true; last = hash; return }
        if (hash == last) return
        last = hash
        if (sample.sensitive || text.isBlank() || text.toByteArray(Charsets.UTF_8).size > ClipHelperRules.MAX_BYTES || !ClipHelperRules.canTravel(text)) return
        emit(text)
    }

    private fun hash(text: String): String =
        MessageDigest.getInstance("SHA-256").digest(text.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
}
