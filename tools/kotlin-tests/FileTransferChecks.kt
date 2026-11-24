package net.oxog.phonebox.deskpanel

// JVM checks for the file transfer's pure parts (FileTransferRules.kt): which shared URIs are acceptable, how a name, a type
// and a size are turned into a request or a place to save, and how the agent's file events are read.
// Run with: python3 tools/test-bridge-checks.py

private var passed = 0
private fun check(name: String, ok: Boolean) {
    if (!ok) throw AssertionError("FAIL: $name")
    passed++
    println("PASS: $name")
}

private const val ID = "0123456789abcdef0123456789abcdef"
private val OWN = setOf("net.oxog.phonebox.deskpanel.usbkey")

private fun uriChecks() {
    check("a media provider's content URI is acceptable", FileTransferRules.acceptableUri("content", "com.android.providers.media.documents", OWN))
    check("the scheme is compared without case", FileTransferRules.acceptableUri("CONTENT", "com.example.files", OWN))
    check("a file URI is refused", !FileTransferRules.acceptableUri("file", null, OWN))
    check("a file URI with an authority is refused", !FileTransferRules.acceptableUri("file", "localhost", OWN))
    check("no scheme is refused", !FileTransferRules.acceptableUri(null, "com.example.files", OWN))
    check("this app's own provider is refused", !FileTransferRules.acceptableUri("content", "net.oxog.phonebox.deskpanel.usbkey", OWN))
    check("this app's own provider is refused whatever its case", !FileTransferRules.acceptableUri("content", "Net.Oxog.PhoneBox.DeskPanel.UsbKey", OWN))
    check("a user id in front of this app's provider is refused", !FileTransferRules.acceptableUri("content", "10@net.oxog.phonebox.deskpanel.usbkey", OWN))
    check("any authority with an at sign is refused", !FileTransferRules.acceptableUri("content", "10@com.example.files", OWN))
    check("no authority is refused", !FileTransferRules.acceptableUri("content", null, OWN))
    check("an empty authority is refused", !FileTransferRules.acceptableUri("content", "", OWN))
}

private fun nameChecks() {
    check("a name keeps only its last part", FileTransferRules.displayName("../a/b.jpg") == "b.jpg")
    check("a Windows path keeps only its last part", FileTransferRules.displayName("C:\\Users\\me\\x.png") == "x.png")
    check("no name becomes file", FileTransferRules.displayName(null) == "file")
    check("a blank name becomes file", FileTransferRules.displayName("  ") == "file")
    check("control characters are removed", FileTransferRules.displayName("a\u0000b\nc.txt") == "abc.txt")
    val long = FileTransferRules.displayName("a".repeat(5000) + ".jpg")
    check("a long name is cut to 200 bytes and keeps its extension", long.toByteArray(Charsets.UTF_8).size <= 200 && long.endsWith(".jpg"))
    val emoji = FileTransferRules.displayName("😀".repeat(200) + ".png")
    check("a cut never splits an emoji", emoji.toByteArray(Charsets.UTF_8).size <= 200 && !emoji.contains('\uFFFD') &&
        String(emoji.toByteArray(Charsets.UTF_8), Charsets.UTF_8) == emoji)
}

private fun routingChecks() {
    check("an image goes to Pictures", FileTransferRules.targetFor("image/jpeg") == FileTransferRules.Target.PICTURES)
    check("a video goes to Movies", FileTransferRules.targetFor("video/mp4") == FileTransferRules.Target.MOVIES)
    check("a PDF goes to Downloads", FileTransferRules.targetFor("application/pdf") == FileTransferRules.Target.DOWNLOADS)
    check("no type goes to Downloads", FileTransferRules.targetFor(null) == FileTransferRules.Target.DOWNLOADS)
    check("the type is read without case", FileTransferRules.targetFor("IMAGE/PNG") == FileTransferRules.Target.PICTURES)
    check("audio goes to Downloads", FileTransferRules.targetFor("audio/mpeg") == FileTransferRules.Target.DOWNLOADS)
}

private fun pathChecks() {
    check("the upload path encodes the name and carries the size",
        FileTransferRules.uploadPath("a b&c.jpg", 12) == "/api/bridge/files/in?name=a%20b%26c.jpg&size=12")
    check("an unknown size is left out", !FileTransferRules.uploadPath("x", null).contains("size="))
    check("a negative size is left out", !FileTransferRules.uploadPath("x", -1).contains("size="))
    check("a zero size is sent", FileTransferRules.uploadPath("x", 0).endsWith("&size=0"))
    check("non-ASCII names are percent-encoded as UTF-8", FileTransferRules.uploadPath("Φωτο.jpg", null) == "/api/bridge/files/in?name=%CE%A6%CF%89%CF%84%CE%BF.jpg")
    check("the download path", FileTransferRules.downloadPath(ID) == "/api/bridge/files/out/$ID")
    check("the done path", FileTransferRules.donePath(ID) == "/api/bridge/files/out/$ID/done")
}

private fun eventChecks() {
    val text = """{\"id\":\"$ID\",\"name\":\"a.jpg\",\"size\":1234,\"mime\":\"image/jpeg\"}"""
    val file = FileTransferRules.parseFileEvent("file", """{"seq":4,"text":"$text"}""")
    check("a file event gives its fields", file == FileTransferRules.IncomingFile(ID, "a.jpg", 1234L, "image/jpeg"))
    check("a clipboard event is not a file event", FileTransferRules.parseFileEvent("clipboard", """{"seq":4,"text":"$text"}""") == null)
    check("an id with a slash is refused", FileTransferRules.parseFileEvent("file",
        """{"seq":1,"text":"{\"id\":\"../x\",\"name\":\"a\",\"size\":1,\"mime\":\"a/b\"}"}""") == null)
    check("an id over 64 characters is refused", FileTransferRules.parseFileEvent("file",
        """{"seq":1,"text":"{\"id\":\"${"a".repeat(65)}\",\"name\":\"a\",\"size\":1,\"mime\":\"a/b\"}"}""") == null)
    check("a negative size is refused", FileTransferRules.parseFileEvent("file",
        """{"seq":1,"text":"{\"id\":\"$ID\",\"name\":\"a\",\"size\":-1,\"mime\":\"a/b\"}"}""") == null)
    check("a size that is not a number is refused", FileTransferRules.parseFileEvent("file",
        """{"seq":1,"text":"{\"id\":\"$ID\",\"name\":\"a\",\"size\":\"1\",\"mime\":\"a/b\"}"}""") == null)
    check("a frame that is not JSON is refused", FileTransferRules.parseFileEvent("file", "nope") == null)
    check("a name with a path is cut to its last part", FileTransferRules.parseFileEvent("file",
        """{"seq":1,"text":"{\"id\":\"$ID\",\"name\":\"../../x.jpg\",\"size\":1,\"mime\":\"image/jpeg\"}"}""")?.name == "x.jpg")
    check("a missing type becomes octet-stream", FileTransferRules.parseFileEvent("file",
        """{"seq":1,"text":"{\"id\":\"$ID\",\"name\":\"a\",\"size\":1}"}""")?.mime == "application/octet-stream")
}

private fun resultChecks() {
    check("507 is no space", BridgeClip.resultFor(507) == SendResult.NO_SPACE)
    check("429 is busy", BridgeClip.resultFor(429) == SendResult.BUSY)
    check("413 is too large", BridgeClip.resultFor(413) == SendResult.TOO_LARGE)
    check("409 is unknown", BridgeClip.resultFor(409) == SendResult.UNKNOWN)
    check("200 is still ok", BridgeClip.resultFor(200) == SendResult.OK)
}

fun main() {
    uriChecks()
    nameChecks()
    routingChecks()
    pathChecks()
    eventChecks()
    resultChecks()
    println("ALL $passed CHECKS PASSED")
}
