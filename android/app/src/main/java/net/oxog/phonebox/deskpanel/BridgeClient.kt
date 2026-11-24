package net.oxog.phonebox.deskpanel

/** Talks to the agent's /api/bridge endpoints over whichever route is up (USB, or Wi-Fi after pairing). */
class BridgeClient(private val link: AgentLink) {
    /** Sends one notification (the JSON from [BridgeClip.notificationBody]). Never throws: a failure is [SendResult.UNREACHABLE]. */
    fun sendNotification(body: String): SendResult = try {
        val reply = link.request("POST", "/api/bridge/notification", body.toByteArray(Charsets.UTF_8), "application/json")
        reply.body.close()
        BridgeClip.resultFor(reply.code)
    } catch (_: Exception) {
        SendResult.UNREACHABLE
    }

    /** The agent's public config as text, or null when it cannot be reached. */
    fun fetchConfig(): String? = try {
        val reply = link.request("GET", "/api/config")
        if (reply.code != 200) { reply.body.close(); null } else reply.body.bufferedReader(Charsets.UTF_8).use { it.readText() }
    } catch (_: Exception) {
        null
    }

    /** Sends [text] to the computer's clipboard. Never throws: a failure is [SendResult.UNREACHABLE]. */
    fun sendClipboard(text: String): SendResult = try {
        val reply = link.request("POST", "/api/bridge/clipboard", BridgeClip.requestBody(text).toByteArray(Charsets.UTF_8), "application/json")
        try { reply.body.close() } catch (_: Exception) {} // a failed close is not a failed send: the agent already answered
        BridgeClip.resultFor(reply.code)
    } catch (_: Exception) {
        SendResult.UNREACHABLE
    }

    /**
     * Streams one file to the Mac (the body is read from [open] while it is sent, never held in memory). [size] is sent when it is
     * known; [onProgress] gets the bytes sent so far; [cancelled] is asked before every chunk. Never throws: a failure is
     * [SendResult.UNREACHABLE], and a cancelled upload is whatever the link answered (the caller knows it cancelled).
     */
    fun uploadFile(name: String, size: Long?, open: () -> java.io.InputStream, onProgress: (Long) -> Unit, cancelled: () -> Boolean): SendResult = try {
        val reply = link.upload(FileTransferRules.uploadPath(FileTransferRules.displayName(name), size), "application/octet-stream", size, open, onProgress, cancelled)
        try { reply.body.close() } catch (_: Exception) {}
        BridgeClip.resultFor(reply.code)
    } catch (_: Exception) {
        SendResult.UNREACHABLE
    }

    /** The files the Mac offers (GET /api/bridge/files/out), or null when it cannot be reached or refuses (its switch is off). */
    fun listOutbox(): List<FileTransferRules.IncomingFile>? = try {
        val reply = link.request("GET", "/api/bridge/files/out")
        if (reply.code != 200) { reply.body.close(); null }
        else reply.body.bufferedReader(Charsets.UTF_8).use { FileTransferRules.parseOutboxList(it.readText()) }
    } catch (_: Exception) {
        null
    }

    /** Tells the Mac this phone has the whole file, so it deletes its copy. Never throws. */
    fun markDone(id: String): SendResult = try {
        val reply = link.request("POST", FileTransferRules.donePath(id))
        try { reply.body.close() } catch (_: Exception) {}
        BridgeClip.resultFor(reply.code)
    } catch (_: Exception) {
        SendResult.UNREACHABLE
    }
}
