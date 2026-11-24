package net.oxog.phonebox.deskpanel


// JVM checks for the automatic phone -> Mac clipboard: ClipHelperRules (what the helper sends and how it reads the answer), ClipEcho (a text the
// service just wrote is not sent back to the Mac) and ClipWatcher (which clipboard changes the helper reports).
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

private class Clip(var text: String?, var sensitive: Boolean = false, var unreadable: Boolean = false)

private fun watcherChecks() {
    val clip = Clip("already on the clipboard")
    val seen = mutableListOf<String>()
    val w = ClipWatcher({ if (clip.unreadable) null else ClipSample(clip.text, clip.sensitive) }) { seen.add(it) }
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
    check("a copy is one argument, whatever it holds", call.takeLast(2) == listOf("--arg", greek))
    check("the call goes to the app's own provider as user 0, by the content command", call.take(2) == listOf("content", "call") && call.containsAll(listOf("--uri", "content://net.oxog.phonebox.deskpanel.clip", "--method", "copy", "--user", "0")))
    check("text that looks like an option stays the value of --arg", ClipHelperRules.callCommand("--uri").takeLast(2) == listOf("--arg", "--uri"))
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
}

fun main() {
    rulesChecks()
    echoChecks()
    watcherChecks()
    println("ALL $passed CHECKS PASSED")
}
