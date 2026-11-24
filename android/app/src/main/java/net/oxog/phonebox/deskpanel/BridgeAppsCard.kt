package net.oxog.phonebox.deskpanel

import android.app.Activity
import android.content.Intent
import android.graphics.Typeface
import android.text.Editable
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.Switch
import android.widget.TextView

/**
 * The apps whose notifications go to the Mac, on the Phone bridge screen itself: "All apps", a search box and every launchable app
 * with a switch (the ones that are on first). A change is saved at once. Nothing is on until the owner turns it on, except the
 * phone's own dialer and messaging app the first time notifications are enabled.
 */
class BridgeAppsCard(private val activity: Activity, private val theme: BridgeTheme) {
    private class Row(val pkg: String, val label: String, val view: View)

    val view = LinearLayout(activity).apply {
        orientation = LinearLayout.VERTICAL
        background = theme.roundRect(theme.card, 16, theme.cardStroke)
        setPadding(theme.dp(16), theme.dp(10), theme.dp(16), theme.dp(12))
    }
    private val list = LinearLayout(activity).apply { orientation = LinearLayout.VERTICAL }
    private lateinit var search: EditText
    private val rows = ArrayList<Row>()
    private val allowed = HashSet<String>()
    private var all = BridgePrefs.allApps(activity)

    init {
        view.addView(TextView(activity).apply {
            text = activity.getString(R.string.bridge_apps_title)
            textSize = 17f
            setTextColor(theme.text)
            typeface = Typeface.DEFAULT_BOLD
            minimumHeight = theme.dp(40)
            gravity = Gravity.CENTER_VERTICAL
        })
        view.addView(Switch(activity).apply {
            text = activity.getString(R.string.bridge_apps_all)
            textSize = 16f
            setTextColor(theme.text)
            minimumHeight = theme.dp(48)
            isChecked = all
            setOnCheckedChangeListener { _, on ->
                all = on
                BridgePrefs.setAllApps(activity, on)
                applyAllState() // the rows' switches are disabled (and read so by a screen reader) while all apps is on
            }
        })
        search = EditText(activity).apply {
            hint = activity.getString(android.R.string.search_go)
            setSingleLine()
            textSize = 15f
            setTextColor(theme.text)
            setHintTextColor(theme.muted)
            background = theme.roundRect(theme.page, 10)
            setPadding(theme.dp(12), theme.dp(8), theme.dp(12), theme.dp(8))
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun afterTextChanged(s: Editable?) = filter(s?.toString() ?: "")
            })
        }
        view.addView(search, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = theme.dp(6); bottomMargin = theme.dp(6) })
        view.addView(list)
    }

    /** Reads the app list off the main thread (loading every label and icon takes a moment) and fills the card. */
    fun load() {
        Thread {
            val pm = activity.packageManager
            val on = BridgePrefs.allowedApps(activity)
            val launcher = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
            val found = pm.queryIntentActivities(launcher, 0)
                .filter { it.activityInfo.packageName != activity.packageName }
                .map { Triple(it.activityInfo.packageName, it.loadLabel(pm).toString(), it.loadIcon(pm)) }
                .distinctBy { it.first }
                .sortedWith(compareBy<Triple<String, String, android.graphics.drawable.Drawable>>({ it.first !in on }, { it.second.lowercase() }))
            activity.runOnUiThread {
                if (activity.isDestroyed) return@runOnUiThread
                allowed.clear(); allowed.addAll(on)
                found.forEach { (pkg, label, icon) -> val row = buildRow(pkg, label, icon); rows.add(Row(pkg, label, row)); list.addView(row) }
                applyAllState()
            }
        }.start()
    }

    private fun buildRow(pkg: String, label: String, icon: android.graphics.drawable.Drawable): View = LinearLayout(activity).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        setPadding(0, theme.dp(6), 0, theme.dp(6))
        addView(ImageView(activity).apply { id = R.id.bridge_app_icon; setImageDrawable(icon) }, LinearLayout.LayoutParams(theme.dp(36), theme.dp(36)))
        addView(TextView(activity).apply { id = R.id.bridge_app_label; text = label; textSize = 16f; setTextColor(theme.text); setPadding(theme.dp(14), 0, theme.dp(8), 0) }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        addView(Switch(activity).apply {
            id = R.id.bridge_app_switch
            isChecked = pkg in allowed
            setOnCheckedChangeListener { _, on ->
                if (on) allowed.add(pkg) else allowed.remove(pkg)
                BridgePrefs.setAllowedApps(activity, allowed)
            }
        })
    }

    /** While "all apps" is on the list and the search have nothing to say, so they are not shown. */
    private fun applyAllState() {
        val shown = if (all) View.GONE else View.VISIBLE
        list.visibility = shown
        search.visibility = shown
        rows.forEach { it.view.findViewById<Switch>(R.id.bridge_app_switch).isEnabled = !all }
    }

    private fun filter(query: String) {
        val q = query.trim().lowercase()
        rows.forEach { it.view.visibility = if (q.isEmpty() || it.label.lowercase().contains(q)) View.VISIBLE else View.GONE }
    }
}
