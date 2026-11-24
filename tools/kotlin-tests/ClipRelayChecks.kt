package net.oxog.phonebox.deskpanel


// JVM checks for the automatic phone -> Mac clipboard: ClipHelperRules (what the helper sends and how it reads the answer, when its loop
// leaves, the command runner's timeout), ClipEcho (a text the service just wrote is not sent back to the Mac), ClipWatcher (which
// clipboard changes the helper reports) and ClipRelay (echoes dropped on arrival, only the newest copy waits).
// Not covered here, only by hand on a phone: the clipboard binder call, the `content` tool itself and the provider's uid check.
// Run with: python3 tools/test-bridge-checks.py

private var passed = 0
private fun check(name: String, ok: Boolean) {
    if (!ok) throw AssertionError("FAIL: $name")
    passed++
    println("PASS: $name")
}

private fun echoChecks() {
    var now = 1_000L
    val echo = ClipEcho(ttlMs = 5_000) { now }
    check("a text nobody wrote is not an echo", !echo.consumeIfEcho("copied on the phone"))
    echo.wrote("from the mac")
    check("a text the service just wrote is an echo", echo.consumeIfEcho("from the mac"))
    check("but only once: copying it again is a real copy", !echo.consumeIfEcho("from the mac"))
    echo.wrote("A"); echo.wrote("B")
    check("two quick writes are both remembered", echo.consumeIfEcho("B") && echo.consumeIfEcho("A"))
    echo.wrote("late")
    now += 5_001
    check("an echo is only recognised for five seconds", !echo.consumeIfEcho("late"))
    echo.wrote("one"); echo.wrote("two"); echo.wrote("three"); echo.wrote("four"); echo.wrote("five")
    check("only the last four writes are kept", !echo.consumeIfEcho("one") && echo.consumeIfEcho("five"))
}

private class Clip(var text: String?, var sensitive: Boolean = false, var unreadable: Boolean = false, var locked: Boolean = false)

private fun watcherChecks() {
    val clip = Clip("already on the clipboard")
    val seen = mutableListOf<String>()
    val w = ClipWatcher({ if (clip.unreadable) null else if (clip.locked) ClipSample(null, false, unknown = true) else ClipSample(clip.text, clip.sensitive) }) { seen.add(it) }
    w.poll()
    check("the first poll only notes what is there: nothing is sent", seen.isEmpty())
    w.poll()
    check("an unchanged clipboard sends nothing", seen.isEmpty())
    clip.text = "copied one"; w.poll(); w.poll()
    check("a copy is sent once", seen == listOf("copied one"))
    clip.text = "copied two"; w.poll()
    check("the next copy is sent too, in order", seen == listOf("copied one", "copied two"))

    clip.text = "password123"; clip.sensitive = true; w.poll()
    clip.sensitive = false; w.poll()
    check("a sensitive copy is never sent, also not on a later poll", seen.size == 2)
    clip.text = "   \n "; w.poll()
    check("blank text is not sent", seen.size == 2)
    clip.text = "a".repeat(ClipHelperRules.MAX_BYTES + 1); w.poll()
    check("text over 64 KiB is not sent", seen.size == 2)
    clip.text = "a".repeat(ClipHelperRules.MAX_BYTES); w.poll()
    check("text of exactly 64 KiB is sent", seen.size == 3)
    clip.text = "before\u0000after"; w.poll()
    check("text with a NUL is not sent", seen.size == 3)

    clip.text = "again"; w.poll()
    clip.text = null; w.poll()            // an image or a file was copied
    clip.text = "again"; w.poll()
    check("the same words copied again after something else are sent again", seen.takeLast(2) == listOf("again", "again"))

    // Android 16 answers nothing while the phone is locked: after unlocking the same text must not be sent again as if copied anew
    clip.text = "locked-in"; w.poll()
    val sentBeforeLock = seen.size
    clip.locked = true; w.poll(); w.poll()
    clip.locked = false; w.poll()
    check("a clipboard that answered nothing (a locked phone) does not make the same text new again", seen.size == sentBeforeLock)
    clip.text = "copied after unlocking"; w.poll()
    check("a copy made after that is still sent", seen.last() == "copied after unlocking")
    val before = seen.size
    clip.unreadable = true; w.poll()
    clip.unreadable = false; w.poll()
    check("a failed read changes nothing", seen.size == before)

    val emoji = "🌍 Φέτα ✓"
    clip.text = emoji; w.poll()
    check("Unicode text is sent unchanged", seen.last() == emoji)
}

private fun rulesChecks() {
    val greek = "Καλημέρα 🌍 -e 'x' \"y\" \$z\nline2"
    val call = ClipHelperRules.callCommand(greek)
    check("a copy is one argument, whatever it holds, behind the marker", call.takeLast(2) == listOf("--arg", ClipHelperRules.COPY_PREFIX + greek))
    check("the call goes to the app's own provider as user 0, by the content command", call.take(2) == listOf("content", "call") && call.containsAll(listOf("--uri", "content://net.oxog.phonebox.deskpanel.clip", "--method", "copy", "--user", "0")))
    check("text that looks like an option stays the value of --arg", ClipHelperRules.callCommand("--uri").takeLast(2) == listOf("--arg", "=--uri"))
    check("no copy ever makes an argument that starts with a dash (the content tool refuses two)",
        listOf("--help", "--uri", "---", "-rf", "--", "-", "=").all { ClipHelperRules.callCommand(it).last().startsWith("-").not() })
    check("no shell is involved: the command is a list of arguments", call.none { it == "sh" || it == "-c" || it == "su" })
    val ping = ClipHelperRules.callCommand(null)
    check("a still-there ping carries no text", ping.contains("ping") && !ping.contains("--arg") && ping.size == 8)
    check("the text appears nowhere else in the command", call.count { greek in it } == 1)

    check("the app's active answer is read", ClipHelperRules.parseReply("Result: Bundle[{state=active}]\n") == ClipHelperRules.Reply.ACTIVE)
    check("the app's off answer is read", ClipHelperRules.parseReply("Result: Bundle[{state=off}]") == ClipHelperRules.Reply.OFF)
    check("no answer at all is none", ClipHelperRules.parseReply("") == ClipHelperRules.Reply.NONE && ClipHelperRules.parseReply("Error: Unable to find provider") == ClipHelperRules.Reply.NONE && ClipHelperRules.parseReply("Result: null") == ClipHelperRules.Reply.NONE)
    check("the last answer counts", ClipHelperRules.parseReply("state=active\nResult: Bundle[{state=off}]") == ClipHelperRules.Reply.OFF)
    check("the app wants copies only while everything is on", ClipHelperRules.stateFor(true, true, false) == "active")
    check("a stopped service, a phone switch that is off or a Mac that is off all mean off", ClipHelperRules.stateFor(false, true, false) == "off" && ClipHelperRules.stateFor(true, false, false) == "off" && ClipHelperRules.stateFor(true, true, true) == "off")
    check("only the shell user's calls are answered", ClipHelperRules.callerAllowed(2000) && !ClipHelperRules.callerAllowed(-1) && !ClipHelperRules.callerAllowed(10234) && !ClipHelperRules.callerAllowed(0) && !ClipHelperRules.callerAllowed(1000))
    check("a copy with a NUL cannot travel", !ClipHelperRules.canTravel("a\u0000b") && ClipHelperRules.canTravel("Καλημέρα 🌍\n\t"))

    var t = 0L
    val latest = LatestCopy(maxAgeMs = 10_000) { t }
    check("nothing waits at first", latest.take() == null)
    latest.put("one"); latest.put("two")
    check("only the newest copy waits, and once", latest.take() == "two" && latest.take() == null)
    latest.put("old"); t += 10_001
    check("a copy that waited over ten seconds is dropped", latest.take() == null)
    latest.put("fresh"); t += 10_000
    check("a copy of exactly ten seconds is still sent", latest.take() == "fresh")

    fun args(vararg types: Class<*>) = ClipHelperRules.clipboardArgs(arrayOf(*types)).toList()
    val s = String::class.java
    val i = Int::class.javaPrimitiveType!!
    check("(package) is the shell's package", args(s) == listOf("com.android.shell"))
    check("(package, user)", args(s, i) == listOf("com.android.shell", 0))
    check("(package, tag, user)", args(s, s, i) == listOf("com.android.shell", null, 0))
    check("(package, tag, user, device) on current Android", args(s, s, i, i) == listOf("com.android.shell", null, 0, 0))
    check("(package, user, tag) as some makers have it", args(s, i, s) == listOf("com.android.shell", 0, null))

    check("the APK is the first entry of the class path", ClipHelperRules.apkPath("/data/app/x/base.apk:/other.jar") == "/data/app/x/base.apk")
    check("no class path, no APK", ClipHelperRules.apkPath(null) == null && ClipHelperRules.apkPath("") == null)
    check("a helper whose APK still exists stays", !ClipHelperRules.apkIsGone("/data/app/x/base.apk") { true })
    check("a helper whose APK was removed or replaced leaves", ClipHelperRules.apkIsGone("/data/app/x/base.apk") { false })
    check("a helper that does not know its APK stays", !ClipHelperRules.apkIsGone(null) { false })

    val streak = ClipHelperRules.Streak(3)
    check("two bad results in a row are not enough", !streak.hit(true) && !streak.hit(true))
    check("the third is", streak.hit(true))
    val reset = ClipHelperRules.Streak(2)
    check("a good result starts the count again", !reset.hit(true) && !reset.hit(false) && !reset.hit(true) && reset.hit(true))

    check("a copy is handed on while the app wants copies, without the marker", ClipHelperRules.copyToRelay("active", "copy", "=x") == "x")
    check("a copy that keeps a leading marker of its own comes out whole", ClipHelperRules.copyToRelay("active", "copy", "==x") == "=x" && ClipHelperRules.copyToRelay("active", "copy", "=--help") == "--help")
    check("a call without the marker hands nothing on", ClipHelperRules.copyToRelay("active", "copy", "x") == null)
    check("a ping hands nothing on", ClipHelperRules.copyToRelay("active", "ping", null) == null && ClipHelperRules.copyToRelay("active", "ping", "=x") == null)
    check("an app that is off hands nothing on", ClipHelperRules.copyToRelay("off", "copy", "=x") == null)
    check("a copy without text hands nothing on", ClipHelperRules.copyToRelay("active", "copy", null) == null)
}

/** A clipboard, an app and an APK for [ClipHelperRules.Loop]: what each call to the app got, and what the app answers. */
private class FakePhone {
    var clip: ClipSample? = ClipSample("already there", false)
    var apkGone = false
    val answers = ArrayDeque<ClipHelperRules.Reply>()
    val calls = mutableListOf<String?>()
    fun loop() = ClipHelperRules.Loop({ clip }, { calls.add(it); answers.removeFirstOrNull() ?: ClipHelperRules.Reply.ACTIVE }, { apkGone }, pingEvery = 4, unreadableLimit = 3)
}

private fun loopChecks() {
    val active = ClipHelperRules.Reply.ACTIVE
    val off = ClipHelperRules.Reply.OFF
    val none = ClipHelperRules.Reply.NONE

    var p = FakePhone(); var loop = p.loop()
    check("the first look sends nothing and the helper stays", loop.step() == null && p.calls.isEmpty())
    p.clip = ClipSample("copied", false)
    check("a copy goes to the app and the helper stays while it is active", loop.step() == null && p.calls == listOf("copied"))
    loop.step(); loop.step()
    check("every few looks it asks 'still there?', with no text", p.calls == listOf("copied", null))

    p = FakePhone(); loop = p.loop(); loop.step()
    p.answers.add(off); p.clip = ClipSample("copied", false)
    check("an 'off' answer to a copy ends it at once", loop.step() == 0)

    p = FakePhone(); loop = p.loop()
    p.answers.add(off)
    check("an 'off' answer to a ping ends it", (1..4).map { loop.step() } == listOf(null, null, null, 0) && p.calls == listOf(null))

    p = FakePhone(); loop = p.loop(); loop.step()
    p.answers.addAll(listOf(none, none)); p.clip = ClipSample("copied", false)
    check("one non-answer is not enough", loop.step() == null)
    check("a second one in a row (here the next ping) ends it", (1..2).map { loop.step() } == listOf(null, 0) && p.calls == listOf("copied", null))

    p = FakePhone(); loop = p.loop(); loop.step()
    p.answers.addAll(listOf(none, active, none)); p.clip = ClipSample("one", false); loop.step()
    p.clip = ClipSample("two", false); loop.step()
    p.clip = ClipSample("three", false)
    check("an answer in between starts the count again", loop.step() == null && p.calls.take(3) == listOf("one", "two", "three"))

    p = FakePhone(); loop = p.loop(); loop.step()
    p.apkGone = true; p.clip = ClipSample("copied after the app was removed", false)
    check("a copy made after the APK is gone is not handed to whoever has the provider's name now", loop.step() == 0 && p.calls.isEmpty())

    p = FakePhone(); loop = p.loop()
    loop.step(); loop.step(); loop.step(); p.apkGone = true
    check("an APK gone by the ping ends it without a call", loop.step() == 0 && p.calls.isEmpty())

    p = FakePhone(); loop = p.loop(); loop.step()
    p.clip = null
    check("a clipboard that cannot be read twice keeps it", loop.step() == null && loop.step() == null)
    p.clip = ClipSample("already there", false)
    check("a good read starts that count again", loop.step() == null)
    p.clip = null
    check("three failed reads in a row end it with 1", loop.step() == null && loop.step() == null && loop.step() == 1)
}

private fun runChecks() {
    if (!java.io.File("/bin/sh").exists()) { println("SKIP: no /bin/sh, the command runner is not checked here"); return }
    val reply = "Result: Bundle[{state=active}]"
    check("what the command prints is read, stdout and stderr", ClipHelperRules.run(listOf("/bin/sh", "-c", "echo '$reply'; echo note >&2"), 5_000) == "$reply\nnote\n")
    val started = System.nanoTime()
    val slow = ClipHelperRules.run(listOf("/bin/sh", "-c", "exec sleep 30"), 300)
    val tookMs = (System.nanoTime() - started) / 1_000_000
    check("a command that does not finish in time is no answer, and the wait ends", slow == null && tookMs < 5_000)
    check("a command that cannot start is no answer", ClipHelperRules.run(listOf("/nonexistent/content"), 1_000) == null)
    check("a long output is drained, not a hang", ClipHelperRules.run(listOf("/bin/sh", "-c", "i=0; while [ \$i -lt 5000 ]; do echo 0123456789012345678901234567890123456789; i=\$((i+1)); done"), 10_000)?.length == 5000 * 41)
}

/** The relay object itself, with its worker thread: echoes are dropped on arrival and only the newest copy waits behind a slow send. */
private fun relayChecks() {
    val got = java.util.concurrent.LinkedBlockingQueue<String>()
    fun next(): String? = got.poll(3, java.util.concurrent.TimeUnit.SECONDS)
    fun nothingMore(): Boolean = got.poll(300, java.util.concurrent.TimeUnit.MILLISECONDS) == null
    ClipRelay.sink = { got.add(it) }
    ClipRelay.submit("first")
    check("a copy reaches whoever wants it", next() == "first")

    ClipRelay.wroteFromMac("from the mac")
    ClipRelay.submit("from the mac")
    ClipRelay.submit("after")
    check("the Mac's own copy coming back is dropped, the next copy is not", next() == "after" && nothingMore())

    val inSend = java.util.concurrent.CountDownLatch(1)
    val release = java.util.concurrent.CountDownLatch(1)
    ClipRelay.sink = { if (it == "slow") { inSend.countDown(); release.await(5, java.util.concurrent.TimeUnit.SECONDS) }; got.add(it) }
    ClipRelay.submit("slow")
    inSend.await(5, java.util.concurrent.TimeUnit.SECONDS)
    ClipRelay.submit("waiting 1"); ClipRelay.submit("waiting 2")
    ClipRelay.wroteFromMac("echo"); ClipRelay.submit("echo")
    release.countDown()
    check("behind a slow send only the newest copy waits, and an echo does not push it out", next() == "slow" && next() == "waiting 2" && nothingMore())

    ClipRelay.sink = null
}

fun main() {
    rulesChecks()
    echoChecks()
    watcherChecks()
    loopChecks()
    runChecks()
    relayChecks()
    println("ALL $passed CHECKS PASSED")
}
