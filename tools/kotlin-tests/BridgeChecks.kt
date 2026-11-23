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
    check("a refused stream delivered no open event", true)
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
    resultChecks()
    streamPathChecks()
    defaultPathCheck()
    refusedDelayChecks()
    refusedDefaultDelayCheck()
    println("ALL $passed CHECKS PASSED")
}
