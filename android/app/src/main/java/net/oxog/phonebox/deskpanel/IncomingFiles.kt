package net.oxog.phonebox.deskpanel

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import java.io.File
import java.io.InputStream
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.TimeUnit

/**
 * Takes the files the Mac offers: the stream's "file" event and, when the stream opens, the list of what still waits (a file
 * offered while the phone was away). Each file is downloaded one at a time to the gallery or Downloads, the Mac is told it has
 * arrived (so it deletes its copy), and a notification opens it. A failed download is tried again after 30 seconds, twice. Nothing
 * about a file is logged.
 */
class IncomingFiles(context: Context) {
    private val app = context.applicationContext
    private val queue = FileTransferRules.IncomingQueue()
    private val worker = Executors.newSingleThreadScheduledExecutor()
    private val recent = ArrayList<Long>() // when the last files arrived, for one summary instead of a pile
    private var shown = 0

    fun onEvent(file: FileTransferRules.IncomingFile) {
        if (!BridgePrefs.filesToPhoneEnabled(app) || !queue.offer(file.id)) return
        submit { download(file) }
    }

    /** The stream just opened: ask what is waiting (a file offered while the phone was away is not in the stream's replay). */
    fun onStreamOpen() {
        if (!BridgePrefs.filesToPhoneEnabled(app)) return
        submit {
            val link = AgentLink(app)
            val waiting = try { BridgeClient(link).listOutbox() } finally { link.close() }
            waiting?.forEach { file -> if (queue.offer(file.id)) download(file) }
        }
    }

    fun shutdown() { worker.shutdownNow() }

    private fun submit(task: () -> Unit) {
        try { worker.execute { try { task() } catch (_: Exception) {} } } catch (_: RejectedExecutionException) {}
    }

    private fun download(file: FileTransferRules.IncomingFile) {
        if (!BridgePrefs.filesToPhoneEnabled(app)) return
        val link = AgentLink(app)
        try {
            val reply = link.stream(FileTransferRules.downloadPath(file.id), readTimeoutMs = 60_000)?.second
            if (reply == null || reply.code != 200) { reply?.body?.close(); retryLater(file); return }
            val saved = reply.body.use { MediaSaver.save(app, file, it) }
            if (saved == null) { retryLater(file); return }
            // the file is on the phone: say so (a few tries; if it never gets through the Mac deletes it after an hour)
            val client = BridgeClient(link)
            for (attempt in 1..3) {
                if (client.markDone(file.id) == SendResult.OK) break
                if (attempt < 3) Thread.sleep(2000)
            }
            queue.finished(file.id)
            notifyReceived(file, saved)
        } catch (_: Exception) {
            retryLater(file)
        } finally {
            link.close()
        }
    }

    private fun retryLater(file: FileTransferRules.IncomingFile) {
        if (!queue.failed(file.id)) { notifyFailed(); return }
        try {
            worker.schedule({ try { if (queue.offer(file.id)) download(file) } catch (_: Exception) {} }, 30, TimeUnit.SECONDS)
        } catch (_: RejectedExecutionException) {}
    }

    private fun manager() = app.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager

    private fun builder(): Notification.Builder {
        if (Build.VERSION.SDK_INT >= 26) {
            manager().createNotificationChannel(NotificationChannel(CHANNEL, app.getString(R.string.bridge_files_to_phone_switch), NotificationManager.IMPORTANCE_DEFAULT))
            return Notification.Builder(app, CHANNEL)
        }
        @Suppress("DEPRECATION")
        return Notification.Builder(app)
    }

    private fun notifyReceived(file: FileTransferRules.IncomingFile, saved: MediaSaver.Saved) {
        val immutable = if (Build.VERSION.SDK_INT >= 23) PendingIntent.FLAG_IMMUTABLE else 0
        val open = if (saved.uri != null) {
            val view = Intent(Intent.ACTION_VIEW).setDataAndType(saved.uri, file.mime)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_GRANT_READ_URI_PERMISSION)
            PendingIntent.getActivity(app, file.id.hashCode(), view, immutable or PendingIntent.FLAG_UPDATE_CURRENT)
        } else PendingIntent.getActivity(app, 4, Intent(app, BridgeActivity::class.java), immutable or PendingIntent.FLAG_UPDATE_CURRENT)
        val n = builder().setSmallIcon(R.drawable.ic_tile).setContentTitle(app.getString(R.string.files_received, file.name))
            .setContentIntent(open).setAutoCancel(true).setGroup(GROUP).build()
        val m = manager()
        try {
            m.notify(BASE_ID + (shown++ % 50), n)
            val now = System.currentTimeMillis()
            synchronized(recent) {
                recent.removeAll { now - it > 5000 }
                recent.add(now)
                if (recent.size > 1) {
                    val summary = builder().setSmallIcon(R.drawable.ic_tile).setContentTitle(app.getString(R.string.files_received_many, recent.size))
                        .setGroup(GROUP).setGroupSummary(true).setAutoCancel(true).build()
                    m.notify(SUMMARY_ID, summary)
                }
            }
        } catch (_: Exception) {}
    }

    private fun notifyFailed() {
        try {
            manager().notify(FAILED_ID, builder().setSmallIcon(R.drawable.ic_tile).setContentTitle(app.getString(R.string.files_receive_failed)).setAutoCancel(true).build())
        } catch (_: Exception) {}
    }

    private companion object {
        const val CHANNEL = "files_received"
        const val GROUP = "net.oxog.phonebox.files"
        const val BASE_ID = 100
        const val SUMMARY_ID = 10
        const val FAILED_ID = 11
    }
}

/** Saves a received file where the owner finds it: the gallery for images and videos, Downloads for the rest. */
object MediaSaver {
    /** [uri] opens the file (null before Android 10, where the file sits in the app's own Downloads folder and the tap opens the app). */
    class Saved(val uri: Uri?)

    /** Copies [input] and checks it is the announced size; null (and nothing left behind) when it is not or the phone refused. */
    fun save(context: Context, file: FileTransferRules.IncomingFile, input: InputStream): Saved? =
        try {
            if (Build.VERSION.SDK_INT >= 29) saveShared(context, file, input) else saveLegacy(context, file, input)
        } catch (_: Exception) {
            null
        }

    @android.annotation.TargetApi(29) // only called from Android 10 (see save)
    private fun saveShared(context: Context, file: FileTransferRules.IncomingFile, input: InputStream): Saved? {
        val resolver = context.contentResolver
        val (collection, folder) = when (FileTransferRules.targetFor(file.mime)) {
            FileTransferRules.Target.PICTURES -> MediaStore.Images.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY) to Environment.DIRECTORY_PICTURES
            FileTransferRules.Target.MOVIES -> MediaStore.Video.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY) to Environment.DIRECTORY_MOVIES
            FileTransferRules.Target.DOWNLOADS -> MediaStore.Downloads.EXTERNAL_CONTENT_URI to Environment.DIRECTORY_DOWNLOADS
        }
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, file.name)
            put(MediaStore.MediaColumns.MIME_TYPE, file.mime)
            put(MediaStore.MediaColumns.RELATIVE_PATH, "$folder/PhoneBox")
            put(MediaStore.MediaColumns.IS_PENDING, 1) // hidden from other apps until it is whole
        }
        val uri = resolver.insert(collection, values) ?: return null
        try {
            val copied = resolver.openOutputStream(uri)?.use { out -> FileTransferRules.copy(input, out, {}, { false }) }
            if (copied == null || copied != file.size) { resolver.delete(uri, null, null); return null }
            resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
            return Saved(uri)
        } catch (e: Exception) {
            try { resolver.delete(uri, null, null) } catch (_: Exception) {}
            throw e
        }
    }

    private fun saveLegacy(context: Context, file: FileTransferRules.IncomingFile, input: InputStream): Saved? {
        val dir = File(context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS) ?: return null, "PhoneBox")
        if (!dir.isDirectory && !dir.mkdirs()) return null
        val target = File(dir, FileTransferRules.uniqueName(file.name) { File(dir, it).exists() })
        val copied = target.outputStream().use { out -> FileTransferRules.copy(input, out, {}, { false }) }
        if (copied != file.size) { target.delete(); return null }
        return Saved(null)
    }
}
