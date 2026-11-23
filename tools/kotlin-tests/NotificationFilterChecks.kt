package net.oxog.phonebox.deskpanel

import java.util.concurrent.atomic.AtomicInteger

// JVM checks for the phone's notification rules: NotificationFilter.kt, DeliveryQueue.kt, BridgeConfigCache.kt and the
// nested JSON reader in BridgeClip.kt. Run with: python3 tools/test-bridge-checks.py
//
// UNVERIFIED FIXTURES: the call checks use the documented Android model (a CallStyle notification of type "incoming", or
// an answer action) because the owner's Samsung dialer has not been recorded yet (plan task D0, the live session). Replace
// them with the recorded notifications there.

private var passed = 0
private fun check(name: String, ok: Boolean) {
    if (!ok) throw AssertionError("FAIL: $name")
    passed++
    println("PASS: $name")
}

private const val OWN = "net.oxog.phonebox.deskpanel"

private fun info(
    pkg: String = "com.msg", key: String = "k", ongoing: Boolean = false, groupSummary: Boolean = false,
    category: String? = null, callStyleIncoming: Boolean = false, hasAnswerAction: Boolean = false,
    title: String = "Anna", text: String = "hi",
) = NotificationInfo(pkg, key, ongoing, groupSummary, category, callStyleIncoming, hasAnswerAction, title, text, 1L)

private fun filterChecks() {
    val allowed = mutableSetOf("com.msg", "com.dialer", OWN)
    val f = NotificationFilter({ allowed }, OWN)
    check("a plain message from an allowed app is sent", f.decide(info()) == Decision.SEND)
    check("an app that is not on the list is skipped", f.decide(info(pkg = "com.shop")) == Decision.SKIP)
    check("PhoneBox's own notifications are skipped even if listed", f.decide(info(pkg = OWN)) == Decision.SKIP)
    check("an ongoing notification (media, download) is skipped", f.decide(info(ongoing = true)) == Decision.SKIP)
    check("a group summary is skipped", f.decide(info(groupSummary = true)) == Decision.SKIP)
    check("a notification with no title and no text is skipped", f.decide(info(title = " ", text = "")) == Decision.SKIP)
    check("a title alone is enough", f.decide(info(title = "Anna", text = "")) == Decision.SEND)
    check("a text alone is enough", f.decide(info(title = "", text = "hello")) == Decision.SEND)

    check("an incoming call is sent although dialers post it as ongoing",
        f.decide(info(pkg = "com.dialer", category = "call", ongoing = true, callStyleIncoming = true, title = "Maria", text = "")) == Decision.SEND)
    check("a call that offers an answer action is sent",
        f.decide(info(pkg = "com.dialer", category = "call", ongoing = true, hasAnswerAction = true)) == Decision.SEND)
    check("a call already in progress (no incoming style, no answer action) is skipped",
        f.decide(info(pkg = "com.dialer", category = "call", ongoing = true)) == Decision.SKIP)
    check("a call from a dialer that is not on the list is skipped",
        f.decide(info(pkg = "com.otherdialer", category = "call", callStyleIncoming = true)) == Decision.SKIP)
    check("a missed call is sent as a missed call",
        f.decide(info(pkg = "com.dialer", category = "missed_call", title = "Maria", text = "Missed call")) == Decision.SEND_AS_MISSED_CALL)
    check("a missed call from an unlisted app is skipped", f.decide(info(pkg = "com.x", category = "missed_call")) == Decision.SKIP)

    allowed.remove("com.msg")
    check("the list is read on every decision (removing an app stops it)", f.decide(info()) == Decision.SKIP)
    allowed.add("com.msg")
    check("and adding it back starts it again", f.decide(info()) == Decision.SEND)
}

private fun callKeyChecks() {
    val keys = CallKeys(max = 3)
    check("the first time a call key is seen is true", keys.firstTime("k1"))
    check("the same call key again is false (later updates of one call are ignored)", !keys.firstTime("k1"))
    check("another key is a new call", keys.firstTime("k2"))
    keys.firstTime("k3")
    keys.firstTime("k4") // evicts k1, the oldest
    check("the oldest key is forgotten beyond the cap", keys.firstTime("k1"))
    check("a recent key is still remembered", !keys.firstTime("k3"))
}

private fun queueChecks() {
    var now = 1_000_000L
    val q = DeliveryQueue<String>(max = 20, ttlMs = 120_000) { now }
    for (i in 1..21) q.add("n$i")
    var taken = q.take().map { it.item }
    check("the queue keeps the newest 20", taken.size == 20 && taken.first() == "n2" && taken.last() == "n21")
    check("taking empties the queue", q.take().isEmpty())

    q.add("old")
    now += 119_000
    q.add("fresh")
    now += 2_000 // "old" is now 121 s old, "fresh" 2 s
    taken = q.take().map { it.item }
    check("an item older than two minutes is dropped, a newer one is kept", taken == listOf("fresh"))

    q.add("kept-time")
    val first = q.take().single()
    now += 100_000
    q.add(first.item, first.at) // a failed retry goes back with its original time
    now += 30_000
    check("a retried item keeps its original time and expires on schedule", q.take().isEmpty())
}

private fun jsonChecks() {
    check("notifications true is read", BridgeClip.notificationsOn("""{"bridge":{"clipboard":false,"notifications":true}}""") == true)
    check("notifications false is read", BridgeClip.notificationsOn("""{"bridge":{"notifications":false}}""") == false)
    check("an agent without a bridge section is off", BridgeClip.notificationsOn("""{"pages":[]}""") == false)
    check("a null bridge section is off", BridgeClip.notificationsOn("""{"bridge":null}""") == false)
    check("a text 'true' is not on", BridgeClip.notificationsOn("""{"bridge":{"notifications":"true"}}""") == false)
    check("nested arrays and objects before it are skipped over",
        BridgeClip.notificationsOn("""{"pages":[{"a":[1,2,{"b":"x\"y"}],"c":{"d":[true,false,null]}}],"bridge":{"notifications":true}}""") == true)
    check("garbage is unknown (null), not off", BridgeClip.notificationsOn("not json") == null)
    check("an unterminated config is unknown", BridgeClip.notificationsOn("""{"bridge":{"notifications":true""") == null)
    check("an array at the top is unknown", BridgeClip.notificationsOn("""[{"bridge":{"notifications":true}}]""") == null)
    val deep = "[".repeat(200) + "]".repeat(200)
    check("absurdly deep nesting is refused, not a stack overflow", BridgeClip.notificationsOn("""{"x":$deep}""") == null)
}

private fun configCacheChecks() {
    var now = 5_000_000L
    val fetches = AtomicInteger()
    var body: String? = """{"bridge":{"notifications":true}}"""
    val cache = BridgeConfigCache({ fetches.incrementAndGet(); body }) { now }
    check("the first read asks the agent and finds it on", cache.notificationsOn())
    check("a second read inside a minute asks nothing", cache.notificationsOn() && fetches.get() == 1)
    body = """{"bridge":{"notifications":false}}"""
    now += 59_000
    check("still cached just under a minute", cache.notificationsOn() && fetches.get() == 1)
    now += 2_000
    check("after a minute it asks again and follows the change", !cache.notificationsOn() && fetches.get() == 2)

    cache.markOn()
    check("markOn forgets an old 'off' at once", cache.notificationsOn())

    // an agent that cannot be reached: unknown counts as on (try to send, the queue holds it), asked again after 10 s
    now += 500_000
    val unreachable = AtomicInteger()
    val c2 = BridgeConfigCache({ unreachable.incrementAndGet(); null }) { now }
    check("when the agent cannot be asked, sending is tried", c2.notificationsOn())
    now += 9_000
    c2.notificationsOn()
    check("and it is not asked again before 10 s", unreachable.get() == 1)
    now += 2_000
    c2.notificationsOn()
    check("but it is asked again after 10 s, not a minute", unreachable.get() == 2)

    // a known 'off' survives an unreachable agent
    now += 500_000
    var phase = 0
    val c3 = BridgeConfigCache({ if (phase++ == 0) """{"bridge":{"notifications":false}}""" else null }) { now }
    check("an agent that says off is off", !c3.notificationsOn())
    now += 70_000
    check("and stays off when the next ask fails", !c3.notificationsOn())

    // markOff: a 403 from the agent turns it off at once, and it asks again after a minute
    now += 500_000
    val c4 = BridgeConfigCache({ """{"bridge":{"notifications":true}}""" }) { now }
    check("on before a 403", c4.notificationsOn())
    c4.markOff()
    check("a 403 (markOff) turns it off at once", !c4.notificationsOn())
    now += 61_000
    check("and a minute later it asks again and finds it on", c4.notificationsOn())

    // a body that is not JSON counts as a failed ask
    now += 500_000
    val c5 = BridgeConfigCache({ "<html>not the agent</html>" }) { now }
    check("an answer that is not JSON is unknown, so sending is tried", c5.notificationsOn())
}

fun main() {
    filterChecks()
    callKeyChecks()
    queueChecks()
    jsonChecks()
    configCacheChecks()
    println("ALL $passed CHECKS PASSED")
}
