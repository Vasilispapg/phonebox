package net.oxog.phonebox.deskpanel

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.Uri
import android.os.Build
import android.os.IBinder
import android.provider.OpenableColumns

/**
 * Sends the files the owner confirmed in [SendFilesToMacActivity], one after another, from a foreground service so a closed window
 * does not stop a big video. The files are read while they are sent (never held in memory), with a progress notification that can
 * cancel, and a last notification that says how it went. Nothing about the files is kept or logged.
 */
class FileUploadService : Service() {
    @Volatile private var cancelled = false
    private val queue = FileTransferRules.WorkQueue<Uri>()
    private val lock = Any()
    private var total = 0 // files taken up since the service went idle, for "3 / 5"
    private var done = 0
    private var sent = 0
    private var failure: SendResult? = null
    private var lastStart = 0

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        synchronized(lock) { lastStart = startId }
        if (intent?.action == ACTION_CANCEL) { cancelled = true; queue.clear(); return START_NOT_STICKY }
        val uris = urisOf(intent)
        try { goForeground(progressNotification(0, 0)) } catch (_: Exception) { stopSelf(); return START_NOT_STICKY }
        if (uris.isEmpty() || !BridgePrefs.filesFromPhoneEnabled(this)) {
            // nothing to send; the service stops only when no upload is running (stopSelf(startId) is a no-op while a later start is pending)
            stopSelf(startId)
            return START_NOT_STICKY
        }
        // a share while an upload runs is added to it: the running worker takes it next, nothing is cut off. Under the same lock as the
        // end of a worker, so a batch either joins the running one or starts the next one, and is never left to a worker that is leaving.
        val startWorker = synchronized(lock) { total += uris.size; queue.add(uris) }
        if (startWorker) Thread { run() }.start()
        return START_NOT_STICKY
    }

    private fun urisOf(intent: Intent?): List<Uri> {
        val clip = intent?.clipData ?: return emptyList()
        return (0 until clip.itemCount).mapNotNull { clip.getItemAt(it).uri }
    }

    private fun run() {
        val app = applicationContext
        val link = AgentLink(app)
        try {
            val client = BridgeClient(link)
            while (true) {
                val uri = queue.next() ?: break
                if (cancelled) continue // drain: a cancel also drops what was waiting
                val number = synchronized(lock) { done + 1 }
                val size = sizeOf(uri)
                var result: SendResult
                if (size != null && size > FileTransferRules.MAX_FILE_BYTES) result = SendResult.TOO_LARGE
                else {
                    var lastShown = 0L
                    result = client.uploadFile(nameOf(uri), size, { app.contentResolver.openInputStream(uri) ?: throw java.io.FileNotFoundException() },
                        { sentBytes ->
                            val now = System.currentTimeMillis()
                            if (now - lastShown > 1000) { lastShown = now; notify(progressNotification(number, synchronized(lock) { total }, sentBytes, size)) }
                        }, { cancelled })
                }
                if (cancelled) continue
                synchronized(lock) {
                    done++
                    if (result == SendResult.OK) sent++ else failure = result
                }
                // the Mac will not take more (switch off, disk full, key refused, busy): stop instead of failing each file
                if (result == SendResult.OFF || result == SendResult.NO_SPACE || result == SendResult.PAIR_AGAIN || result == SendResult.BUSY) queue.clear()
            }
        } finally {
            link.close()
        }
        val ending = synchronized(lock) {
            // a batch that arrived as this one ended started a new worker: that one reports for both and stops the service
            if (!queue.idle()) null
            else Triple(sent, failure, lastStart).also { total = 0; done = 0; sent = 0; failure = null }
        } ?: return
        val (count, problem, last) = ending
        val wasCancelled = cancelled
        cancelled = false
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.cancel(PROGRESS_ID)
        if (!wasCancelled) manager.notify(RESULT_ID, resultNotification(count, problem))
        stopSelf(last) // a start request that arrived since has a later id: then the service stays
    }

    private fun sizeOf(uri: Uri): Long? = try {
        contentResolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)?.use { c ->
            if (c.moveToFirst() && !c.isNull(0)) c.getLong(0).takeIf { it > 0 } else null
        }
    } catch (_: Exception) { null }

    private fun nameOf(uri: Uri): String = try {
        contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
            if (c.moveToFirst() && !c.isNull(0)) c.getString(0) else null
        } ?: uri.lastPathSegment ?: "file"
    } catch (_: Exception) { uri.lastPathSegment ?: "file" }

    private fun notify(n: Notification) {
        try { (getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).notify(PROGRESS_ID, n) } catch (_: Exception) {}
    }

    private fun goForeground(n: Notification) {
        if (Build.VERSION.SDK_INT >= 29) startForeground(PROGRESS_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        else startForeground(PROGRESS_ID, n)
    }

    private fun builder(): Notification.Builder {
        if (Build.VERSION.SDK_INT >= 26) {
            val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            manager.createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.files_uploading), NotificationManager.IMPORTANCE_LOW))
            return Notification.Builder(this, CHANNEL)
        }
        @Suppress("DEPRECATION")
        return Notification.Builder(this)
    }

    private fun progressNotification(number: Int, total: Int, done: Long = 0, size: Long? = null): Notification {
        val immutable = if (Build.VERSION.SDK_INT >= 23) PendingIntent.FLAG_IMMUTABLE else 0
        val cancel = PendingIntent.getService(this, 2, Intent(this, FileUploadService::class.java).setAction(ACTION_CANCEL), immutable or PendingIntent.FLAG_UPDATE_CURRENT)
        val b = builder().setSmallIcon(R.drawable.ic_tile).setContentTitle(getString(R.string.files_uploading)).setOngoing(true)
        if (total > 1) b.setContentText("$number / $total")
        if (size != null && size > 0) b.setProgress(100, (done * 100 / size).toInt().coerceIn(0, 100), false) else b.setProgress(0, 0, true)
        @Suppress("DEPRECATION") // the Icon-based Action builder is API 23, the app installs on 21
        b.addAction(R.drawable.ic_tile, getString(R.string.pair_negative), cancel)
        return b.build()
    }

    private fun resultNotification(sent: Int, failure: SendResult?): Notification {
        val text = when {
            failure == null -> getString(R.string.files_sent, sent)
            else -> getString(when (failure) {
                SendResult.OFF -> R.string.files_send_off
                SendResult.NO_SPACE -> R.string.files_send_no_space
                SendResult.BUSY -> R.string.files_send_busy
                SendResult.TOO_LARGE -> R.string.files_send_too_large
                SendResult.PAIR_AGAIN -> R.string.bridge_pair_again
                else -> R.string.files_send_failed
            })
        }
        val immutable = if (Build.VERSION.SDK_INT >= 23) PendingIntent.FLAG_IMMUTABLE else 0
        val open = PendingIntent.getActivity(this, 3, Intent(this, BridgeActivity::class.java), immutable or PendingIntent.FLAG_UPDATE_CURRENT)
        val b = builder().setSmallIcon(R.drawable.ic_tile).setContentText(text).setContentIntent(open).setAutoCancel(true)
        if (sent > 0 && failure != null) b.setContentTitle(getString(R.string.files_sent, sent)) else b.setContentTitle(getString(R.string.files_uploading))
        return b.build()
    }

    /** Android 15 ends a data-sync foreground service after about six hours a day; stop cleanly instead of being killed. */
    override fun onTimeout(startId: Int, fgsType: Int) { cancelled = true; queue.clear(); stopSelf() }

    override fun onDestroy() {
        cancelled = true
        queue.clear()
        super.onDestroy()
    }

    companion object {
        private const val CHANNEL = "files"
        private const val PROGRESS_ID = 8
        private const val RESULT_ID = 9
        private const val ACTION_CANCEL = "net.oxog.phonebox.deskpanel.CANCEL_UPLOAD"

        /** Starts the upload of [uris] (content URIs the caller was granted); the grant is passed on to the service through the clip. */
        fun start(context: Context, uris: List<Uri>) {
            if (uris.isEmpty()) return
            val app = context.applicationContext
            val clip = ClipData.newRawUri("files", uris.first())
            uris.drop(1).forEach { clip.addItem(ClipData.Item(it)) }
            val intent = Intent(app, FileUploadService::class.java).apply { clipData = clip; addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION) }
            try {
                if (Build.VERSION.SDK_INT >= 26) app.startForegroundService(intent) else app.startService(intent)
            } catch (e: Exception) { BridgePrefs.lastError = e.javaClass.simpleName }
        }
    }
}
