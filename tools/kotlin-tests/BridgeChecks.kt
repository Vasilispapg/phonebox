package net.oxog.phonebox.deskpanel

import java.io.ByteArrayInputStream
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

// JVM checks for the phone bridge's pure parts (BridgeClip.kt) and for AgentStream's new path and refusal delay.
// Run with: python3 tools/test-bridge-checks.py

private var passed = 0
private fun check(name: String, ok: Boolean) {
    if (!ok) throw AssertionError("FAIL: $name")
    passed++
    println("PASS: $name")
}

private fun shouldSendChecks() {
    check("null text is not sent", !BridgeClip.shouldSend(null, false))
    check("blank text is not sent", !BridgeClip.shouldSend("   \n", false))
    check("empty text is not sent", !BridgeClip.shouldSend("", false))
    check("a sensitive clip is not sent", !BridgeClip.shouldSend("hunter2", true))
    check("ordinary text is sent", BridgeClip.shouldSend("hello", false))
    check("exactly 64 KiB of ASCII is sent", BridgeClip.shouldSend("a".repeat(BridgeClip.MAX_BYTES), false))
    check("one byte over 64 KiB is not sent", !BridgeClip.shouldSend("a".repeat(BridgeClip.MAX_BYTES + 1), false))
    // the limit counts UTF-8 bytes: 40 000 Greek letters are 80 000 bytes, 32 768 of them are exactly 64 KiB
    check("40 000 Greek letters are over the limit", !BridgeClip.shouldSend("λ".repeat(40_000), false))
    check("32 768 Greek letters are exactly the limit", BridgeClip.shouldSend("λ".repeat(BridgeClip.MAX_BYTES / 2), false))
    check("16 385 emoji are over the limit", !BridgeClip.shouldSend("🌍".repeat(BridgeClip.MAX_BYTES / 4 + 1), false))
}

private fun parseEventChecks() {
    check("a clipboard event gives its text", BridgeClip.parseEvent("clipboard", """{"seq":3,"text":"hello"}""") == "hello")
    check("escaped newlines come back as newlines", BridgeClip.parseEvent("clipboard", """{"seq":3,"text":"a\nb\r\nc\td"}""") == "a\nb\r\nc\td")
    check("escaped quotes and backslashes", BridgeClip.parseEvent("clipboard", """{"seq":1,"text":"say \"hi\" C:\\dir"}""") == "say \"hi\" C:\\dir")
    check("a surrogate pair becomes one emoji", BridgeClip.parseEvent("clipboard", """{"seq":1,"text":"\ud83c\udf0d"}""") == "🌍")
    check("Greek by \\u escapes", BridgeClip.parseEvent("clipboard", """{"seq":1,"text":"\u039a\u03b1\u03bb\u03b7"}""") == "Καλη")
    check("raw non-ASCII passes through", BridgeClip.parseEvent("clipboard", "{\"seq\":1,\"text\":\"Καλημέρα ✓\"}") == "Καλημέρα ✓")
    check("a key named text inside the text is not mistaken for the field",
        BridgeClip.parseEvent("clipboard", """{"seq":2,"text":"{\"text\":\"evil\"}"}""") == """{"text":"evil"}""")
    check("another event name gives null", BridgeClip.parseEvent("state", """{"seq":3,"text":"hello"}""") == null)
    check("an event without text gives null", BridgeClip.parseEvent("clipboard", """{"seq":3}""") == null)
    check("a non-string text gives null", BridgeClip.parseEvent("clipboard", """{"seq":3,"text":5}""") == null)
    check("an empty text gives null", BridgeClip.parseEvent("clipboard", """{"seq":3,"text":""}""") == null)
    check("broken JSON gives null", BridgeClip.parseEvent("clipboard", """{"seq":3,"text":"unterminated""") == null)
    check("an array gives null", BridgeClip.parseEvent("clipboard", """["text"]""") == null)
}

private fun requestBodyChecks() {
    val samples = listOf("plain", "say \"hi\"", "back\\slash", "line1\nline2\r\n", "tab\there", "\u0001control\u001f", "Καλημέρα 🌍 ✓", "{\"text\":\"x\"}", "</script>")
    for (s in samples) {
        val body = BridgeClip.requestBody(s)
        check("body for ${s.take(12).replace("\n", "\\n")} is one JSON object with the text", body.startsWith("{\"text\":\"") && body.endsWith("\"}"))
        check("body round-trips ${s.take(12).replace("\n", "\\n")}", BridgeClip.parseEvent("clipboard", body) == s)
        check("body has no raw newline or control byte", body.none { it < ' ' })
    }
}

private fun skipReasonChecks() {
    check("null is skipped as nothing", BridgeClip.skipReason(null, false) == BridgeClip.Skip.NOTHING)
    check("blank is skipped as nothing", BridgeClip.skipReason(" \n ", false) == BridgeClip.Skip.NOTHING)
    check("sensitive is skipped as nothing (private)", BridgeClip.skipReason("hunter2", true) == BridgeClip.Skip.NOTHING)
    check("over the limit is skipped as too large", BridgeClip.skipReason("a".repeat(BridgeClip.MAX_BYTES + 1), false) == BridgeClip.Skip.TOO_LARGE)
    check("a long sensitive clip is private, not too large", BridgeClip.skipReason("a".repeat(BridgeClip.MAX_BYTES + 1), true) == BridgeClip.Skip.NOTHING)
    check("ordinary text has no skip reason", BridgeClip.skipReason("ok", false) == null)
    for (t in listOf<String?>(null, "", " ", "x", "a".repeat(BridgeClip.MAX_BYTES), "a".repeat(BridgeClip.MAX_BYTES + 1)))
        for (sensitive in listOf(false, true))
            check("shouldSend agrees with skipReason for ${t?.length} / $sensitive", BridgeClip.shouldSend(t, sensitive) == (BridgeClip.skipReason(t, sensitive) == null))
}

private fun resultChecks() {
    check("200 is OK", BridgeClip.resultFor(200) == SendResult.OK)
    check("403 means the Mac's switch is off", BridgeClip.resultFor(403) == SendResult.OFF)
    check("413 means too large", BridgeClip.resultFor(413) == SendResult.TOO_LARGE)
    check("503 (the link's offline reply) is unreachable", BridgeClip.resultFor(503) == SendResult.UNREACHABLE)
    check("500 is unreachable", BridgeClip.resultFor(500) == SendResult.UNREACHABLE)
    check("501 (not a Mac) is unreachable", BridgeClip.resultFor(501) == SendResult.UNREACHABLE)
    check("400 is unreachable", BridgeClip.resultFor(400) == SendResult.UNREACHABLE)
}

/** A stand-in for the production AgentLink (tools/kotlin-tests/AgentLink.kt): a code and a body per attempt. */
private fun streamPathChecks() {
    val closed = CountDownLatch(1)
    val link = AgentLink { ByteArrayInputStream(byteArrayOf()) }
    val stream = AgentStream(link, "/api/bridge/stream") { event, _ -> if (event == "close") closed.countDown() }
    stream.start()
    check("the stream opens and closes", closed.await(5, TimeUnit.SECONDS))
    stream.stop()
    check("the stream asked for the path it was given", link.paths.first() == "/api/bridge/stream")
}

private fun defaultPathCheck() {
    val closed = CountDownLatch(1)
    val link = AgentLink { ByteArrayInputStream(byteArrayOf()) }
    val stream = AgentStream(link) { event, _ -> if (event == "close") closed.countDown() }
    stream.start()
    check("the default stream still opens", closed.await(5, TimeUnit.SECONDS))
    stream.stop()
    check("the default path is the panel's stream", link.paths.first() == "/api/stream")
}

private fun stateChecks() {
    check("a refused (403) attempt means the Mac's switch is off", BridgeClip.stateAfterFailure(403) == ClipboardState.MAC_OFF)
    check("501 means this computer has no bridge", BridgeClip.stateAfterFailure(501) == ClipboardState.UNSUPPORTED)
    check("404 (an older agent) means this computer has no bridge", BridgeClip.stateAfterFailure(404) == ClipboardState.UNSUPPORTED)
    check("no route means waiting for the Mac", BridgeClip.stateAfterFailure(null) == ClipboardState.WAITING)
    check("401 (the pairing was forgotten) means waiting", BridgeClip.stateAfterFailure(401) == ClipboardState.WAITING)
    check("503 means waiting", BridgeClip.stateAfterFailure(503) == ClipboardState.WAITING)
    check("a later 'no route' replaces an earlier 403 (the state follows the latest failure)",
        listOf(403, null).fold(ClipboardState.STOPPED) { _, code -> BridgeClip.stateAfterFailure(code) } == ClipboardState.WAITING)
}

private fun readTimeoutChecks() {
    // the bridge stream carries only a ping every 15 s, so it must not be cut by the panel stream's 8 s read timeout
    val bridgeLink = AgentLink { ByteArrayInputStream(byteArrayOf()) }
    val closed = CountDownLatch(1)
    val bridge = AgentStream(bridgeLink, "/api/bridge/stream", readTimeoutMs = 40_000) { event, _ -> if (event == "close") closed.countDown() }
    bridge.start()
    check("the bridge stream opens", closed.await(5, TimeUnit.SECONDS))
    bridge.stop()
    check("the bridge stream asked for a 40 s read timeout", bridgeLink.timeouts.first() == 40_000)

    val panelLink = AgentLink { ByteArrayInputStream(byteArrayOf()) }
    val panelClosed = CountDownLatch(1)
    val panel = AgentStream(panelLink) { event, _ -> if (event == "close") panelClosed.countDown() }
    panel.start()
    check("the panel stream opens", panelClosed.await(5, TimeUnit.SECONDS))
    panel.stop()
    check("the panel stream keeps the 8 s read timeout", panelLink.timeouts.first() == AgentLink.STREAM_READ_TIMEOUT)
}

private fun refusedDelayChecks() {
    // while the Mac answers 403 the stream waits refusedRetryMs, not the 2 s of an ordinary failure
    val times = CopyOnWriteArrayList<Long>()
    val second = CountDownLatch(2)
    val link = AgentLink(code = { times.add(System.nanoTime()); second.countDown(); 403 }) { ByteArrayInputStream(byteArrayOf()) }
    val stream = AgentStream(link, "/api/bridge/stream", refusedRetryMs = 400) { _, _ -> }
    stream.start()
    check("two attempts were made while refused", second.await(5, TimeUnit.SECONDS))
    stream.stop()
    val gapMs = (times[1] - times[0]) / 1_000_000
    check("the retry after a 403 waited about refusedRetryMs ($gapMs ms)", gapMs in 350..1500)
}

private fun onFailureChecks() {
    // every failed attempt is reported with the status the agent answered, or null when it could not be reached
    fun collect(code: () -> Int, offline: () -> Boolean): List<Int?> {
        val seen = CopyOnWriteArrayList<Int?>()
        val three = CountDownLatch(3)
        val link = AgentLink(code = code, offline = offline) { ByteArrayInputStream(byteArrayOf()) }
        val stream = AgentStream(link, "/api/bridge/stream", retryMinMs = 20, retryMaxMs = 20, refusedRetryMs = 20,
            onFailure = { seen.add(it); three.countDown() }) { _, _ -> }
        stream.start()
        three.await(5, TimeUnit.SECONDS)
        stream.stop()
        return seen.take(3)
    }
    check("403 is reported as 403", collect({ 403 }, { false }) == listOf(403, 403, 403))
    check("501 (not a Mac) is reported as 501", collect({ 501 }, { false }) == listOf(501, 501, 501))
    check("no route is reported as null", collect({ 200 }, { true }) == listOf(null, null, null))
}

private fun onFailureStopChecks() {
    // stop() must wait for a failure report already running, so none lands after it returns (the clipboard service sets
    // its state to STOPPED right after stop(): a late report would put a stale "waiting" or "Mac off" over it)
    val entered = CountDownLatch(1)
    val finished = java.util.concurrent.atomic.AtomicBoolean(false)
    val reports = java.util.concurrent.atomic.AtomicInteger()
    val link = AgentLink(code = { 503 }) { ByteArrayInputStream(byteArrayOf()) }
    val stream = AgentStream(link, "/api/bridge/stream", retryMinMs = 20, retryMaxMs = 20, onFailure = {
        if (reports.getAndIncrement() == 0) { entered.countDown(); Thread.sleep(300); finished.set(true) }
    }) { _, _ -> }
    stream.start()
    check("a failure is reported", entered.await(5, TimeUnit.SECONDS))
    stream.stop()
    check("stop() returned only after the report in flight had finished", finished.get())
    val after = reports.get()
    Thread.sleep(200)
    check("no failure is reported after stop() returned", reports.get() == after)
}

private fun backoffChecks() {
    // failures wait 100, 200, 400, 400 ms (doubling to the cap), not a fixed delay
    val times = CopyOnWriteArrayList<Long>()
    val five = CountDownLatch(5)
    val link = AgentLink(code = { times.add(System.nanoTime()); five.countDown(); 503 }) { ByteArrayInputStream(byteArrayOf()) }
    val stream = AgentStream(link, "/api/bridge/stream", retryMinMs = 100, retryMaxMs = 400) { _, _ -> }
    stream.start()
    check("five attempts were made", five.await(10, TimeUnit.SECONDS))
    stream.stop()
    val gaps = (1..4).map { (times[it] - times[it - 1]) / 1_000_000 }
    check("the second wait is longer than the first ($gaps)", gaps[1] >= gaps[0] + 60)
    check("the waits stop growing at the cap ($gaps)", gaps[3] < gaps[2] + 250 && gaps[3] in 300..1500)
}

private fun backoffResetChecks() {
    // an attempt that connects starts the waits over: after 503, 503, an open stream, the next failure waits the minimum again
    val times = CopyOnWriteArrayList<Long>()
    val sequence = listOf(503, 503, 200, 503, 503)
    val five = CountDownLatch(5)
    val link = AgentLink(code = { times.add(System.nanoTime()); five.countDown(); sequence.getOrElse(times.size - 1) { 503 } }) { ByteArrayInputStream(byteArrayOf()) }
    val stream = AgentStream(link, "/api/bridge/stream", retryMinMs = 100, retryMaxMs = 800) { _, _ -> }
    stream.start()
    check("five attempts were made around an open stream", five.await(10, TimeUnit.SECONDS))
    stream.stop()
    val afterReset = (times[4] - times[3]) / 1_000_000
    check("the wait after a good connection is the minimum again ($afterReset ms)", afterReset in 60..300)
}

private fun refusedDefaultDelayCheck() {
    // with the default delay a 403 on the panel's own stream retries as before (2 s)
    val times = CopyOnWriteArrayList<Long>()
    val second = CountDownLatch(2)
    val link = AgentLink(code = { times.add(System.nanoTime()); second.countDown(); 403 }) { ByteArrayInputStream(byteArrayOf()) }
    val stream = AgentStream(link) { _, _ -> }
    stream.start()
    check("two attempts with the default delay", second.await(8, TimeUnit.SECONDS))
    stream.stop()
    val gapMs = (times[1] - times[0]) / 1_000_000
    check("the default retry is still about 2 s ($gapMs ms)", gapMs in 1800..3500)
}

fun main() {
    shouldSendChecks()
    parseEventChecks()
    requestBodyChecks()
    skipReasonChecks()
    resultChecks()
    streamPathChecks()
    defaultPathCheck()
    stateChecks()
    readTimeoutChecks()
    refusedDelayChecks()
    onFailureChecks()
    onFailureStopChecks()
    backoffChecks()
    backoffResetChecks()
    refusedDefaultDelayCheck()
    println("ALL $passed CHECKS PASSED")
}
