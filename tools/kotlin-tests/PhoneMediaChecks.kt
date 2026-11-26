package net.oxog.phonebox.deskpanel

// JVM checks for the phone's own music in the Now playing widget: PhoneMediaModel.kt. Run with: python3 tools/test-bridge-checks.py

private var passed = 0
private fun check(name: String, ok: Boolean) {
    if (!ok) throw AssertionError("FAIL: $name")
    passed++
    println("PASS: $name")
}

private const val OWN = "net.oxog.phonebox.deskpanel"

private fun s(pkg: String, playing: Boolean = false, paused: Boolean = false, title: Boolean = true) = SessionFacts(pkg, playing, paused, title)

private fun track(playing: Boolean = true, pos: Long = 10_000, dur: Long = 200_000, title: String = "Song", rate: Double = 1.0) =
    PhoneTrack("com.spotify.music", "Spotify", title, "Artist", "Album", playing, pos, dur, true, rate, 0)

fun main() {
    // which session is shown
    check("no sessions: nothing", PhoneMediaModel.pick(OWN, emptyList()) == -1)
    check("the playing session wins over a paused one that is listed first",
        PhoneMediaModel.pick(OWN, listOf(s("a", paused = true), s("b", playing = true))) == 1)
    check("with two playing sessions the first one (most recently active) wins",
        PhoneMediaModel.pick(OWN, listOf(s("a", playing = true), s("b", playing = true))) == 0)
    check("a paused session with a title is shown, so its play button can resume it",
        PhoneMediaModel.pick(OWN, listOf(s("a", paused = true))) == 0)
    check("a stopped session (neither playing nor paused) is not shown", PhoneMediaModel.pick(OWN, listOf(s("a"))) == -1)
    check("PhoneBox's own session is never shown", PhoneMediaModel.pick(OWN, listOf(s(OWN, playing = true))) == -1)
    check("a session without a title is not shown", PhoneMediaModel.pick(OWN, listOf(s("a", playing = true, title = false))) == -1)
    check("a session without a title does not hide the next one",
        PhoneMediaModel.pick(OWN, listOf(s("a", playing = true, title = false), s("b", playing = true))) == 1)
    check("states: playing is 3, paused 2, buffering counts as paused",
        PhoneMediaModel.isPlaying(3) && !PhoneMediaModel.isPlaying(2) && PhoneMediaModel.isPaused(2) && PhoneMediaModel.isPaused(6) && !PhoneMediaModel.isPaused(1))

    // where the song is
    check("a paused song stays where it was", PhoneMediaModel.position(5_000, 1_000, 9_000, 1f, false, 100_000) == 5_000L)
    check("a playing song moves on by the time since the state was written", PhoneMediaModel.position(5_000, 1_000, 3_000, 1f, true, 100_000) == 7_000L)
    check("the playback speed counts", PhoneMediaModel.position(5_000, 1_000, 3_000, 2f, true, 100_000) == 9_000L)
    check("a position never passes the length", PhoneMediaModel.position(99_000, 1_000, 9_000, 1f, true, 100_000) == 100_000L)
    check("an unknown position (Android's -1) is 0", PhoneMediaModel.position(-1, 1_000, 9_000, 1f, true, 100_000) == 0L)
    check("a zero or NaN speed counts as normal speed", PhoneMediaModel.position(0, 1_000, 2_000, 0f, true, 0) == 1_000L &&
        PhoneMediaModel.position(0, 1_000, 2_000, Float.NaN, true, 0) == 1_000L)
    check("a state written in the future adds nothing", PhoneMediaModel.position(5_000, 9_000, 1_000, 1f, true, 100_000) == 5_000L)
    check("no length is 0, never negative", PhoneMediaModel.duration(-1) == 0L && PhoneMediaModel.duration(0) == 0L && PhoneMediaModel.duration(5) == 5L)

    // what is worth telling the panel
    val t0 = track()
    check("a first song is sent", PhoneMediaModel.worthSending(null, 0, t0, 0))
    check("nothing after nothing is not sent", !PhoneMediaModel.worthSending(null, 0, null, 0))
    check("the song ending is sent", PhoneMediaModel.worthSending(t0, 0, null, 0))
    check("a repeated state that says what the panel counts is dropped", !PhoneMediaModel.worthSending(t0, 0, t0.copy(posMs = 12_000), 2_000))
    check("a pause is sent", PhoneMediaModel.worthSending(t0, 0, t0.copy(playing = false), 100))
    check("another song is sent", PhoneMediaModel.worthSending(t0, 0, t0.copy(title = "Other"), 100))
    check("a seek is sent", PhoneMediaModel.worthSending(t0, 0, t0.copy(posMs = 90_000), 1_000))
    check("a small drift is not a seek", !PhoneMediaModel.worthSending(t0, 0, t0.copy(posMs = 11_900), 1_000))
    check("a paused song's position is where it stopped, not counted on", PhoneMediaModel.worthSending(t0.copy(playing = false), 0, t0.copy(playing = false, posMs = 20_000), 5_000))

    // the JSON the page reads
    val j = PhoneMediaModel.json(track(title = "He said \"hi\"\n\\ \u2028"))
    check("the JSON marks the song as the phone's", j.contains("\"phone\":true"))
    check("quotes, backslashes, newlines and line separators are escaped", j.contains("\"title\":\"He said \\\"hi\\\"\\n\\\\ \\u2028\""))
    check("a song with no known rate is 1.0", PhoneMediaModel.json(track(rate = 0.0)).contains("\"playbackRate\":1.0"))
    check("nothing playing is null", PhoneMediaModel.json(null) == "null")
    check("control characters are escaped", PhoneMediaModel.quote("a\u0001b") == "\"a\\u0001b\"")
    check("a long title is cut", PhoneMediaModel.cut("x".repeat(1000)).length == 300 && PhoneMediaModel.cut(null) == "" && PhoneMediaModel.cut("  a ") == "a")
    check("only the four buttons are valid", listOf("playpause", "next", "prev", "seek").all(PhoneMediaModel::validAction) &&
        !PhoneMediaModel.validAction("") && !PhoneMediaModel.validAction("stop") && !PhoneMediaModel.validAction("PLAYPAUSE"))
    check("a cover id never equals an agent id", PhoneMediaModel.ART_ID_BASE > 100_000)

    // a reading that waited: the position as of now
    val played = track(pos = 10_000)
    check("a playing song has moved on by the time since it was read", PhoneMediaModel.advance(played, 1_000, 6_000)?.posMs == 15_000L)
    check("the playback speed counts when it moves on", PhoneMediaModel.advance(track(rate = 2.0), 0, 3_000)?.posMs == 16_000L)
    check("it never moves past the length", PhoneMediaModel.advance(track(pos = 199_000), 0, 9_000)?.posMs == 200_000L)
    check("a paused song stays where it was", PhoneMediaModel.advance(track(playing = false), 0, 9_000)?.posMs == 10_000L)
    check("no song stays no song, and a clock that went back changes nothing", PhoneMediaModel.advance(null, 0, 5) == null && PhoneMediaModel.advance(played, 9_000, 1_000)?.posMs == 10_000L)

    // a speed that makes no sense counts as normal speed, in position() and in advance() alike
    check("a zero, negative or non-finite speed is 1.0", PhoneMediaModel.rate(0f) == 1.0 && PhoneMediaModel.rate(-2f) == 1.0 &&
        PhoneMediaModel.rate(Float.NaN) == 1.0 && PhoneMediaModel.rate(Float.POSITIVE_INFINITY) == 1.0 && PhoneMediaModel.rate(1.5f) == 1.5)
    check("advance() with a speed of 0 or NaN still moves at normal speed", PhoneMediaModel.advance(track(rate = 0.0), 0, 4_000)?.posMs == 14_000L &&
        PhoneMediaModel.advance(track(rate = Double.NaN), 0, 4_000)?.posMs == 14_000L)

    // seeking
    check("a seek goes to where it was asked, within the song", PhoneMediaModel.seekTarget(5_000, 200_000) == 5_000L)
    check("a seek is held at the start and at the end", PhoneMediaModel.seekTarget(-3, 200_000) == 0L && PhoneMediaModel.seekTarget(900_000, 200_000) == 200_000L)
    check("with no known length a seek is only held at the start", PhoneMediaModel.seekTarget(900_000, 0) == 900_000L && PhoneMediaModel.seekTarget(-1, 0) == 0L)

    // the phone's media volume
    check("the phone's 15 steps map onto 0-100", PhoneMediaModel.volumePercent(0, 15) == 0 && PhoneMediaModel.volumePercent(15, 15) == 100 && PhoneMediaModel.volumePercent(8, 15) == 53)
    check("a level goes to the nearest step and back", PhoneMediaModel.volumeStep(50, 15) == 8 && PhoneMediaModel.volumeStep(100, 15) == 15 && PhoneMediaModel.volumeStep(0, 15) == 0)
    check("out-of-range levels are held in range", PhoneMediaModel.volumeStep(250, 15) == 15 && PhoneMediaModel.volumeStep(-5, 15) == 0 && PhoneMediaModel.volumePercent(99, 15) == 100)
    check("no steps (max 0) is 0, not a division by zero", PhoneMediaModel.volumePercent(3, 0) == 0 && PhoneMediaModel.volumeStep(50, 0) == 0)
    check("the audio JSON has the agent's fields", PhoneMediaModel.audioJson(15, 15, false) == "{\"volume\":100,\"muted\":false,\"canControl\":true,\"phone\":true}" &&
        PhoneMediaModel.audioJson(0, 15, true).contains("\"muted\":true"))

    println("$passed phone media checks passed")
}
