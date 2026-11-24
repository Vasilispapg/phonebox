package net.oxog.phonebox.deskpanel

import android.app.Activity
import android.content.Intent
import android.content.pm.ResolveInfo
import android.os.Bundle
import android.view.View
import android.view.ViewGroup
import android.widget.BaseAdapter
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ListView
import android.widget.Switch
import android.widget.TextView

/**
 * The apps whose notifications go to the Mac: every launchable app with a switch. A change is saved at once. Nothing is on
 * until the owner turns it on, except the phone's own dialer and messaging app the first time notifications are enabled.
 */
class BridgeAppsActivity : Activity() {
    private class Row(val pkg: String, val label: String, val info: ResolveInfo)

    private lateinit var rows: List<Row>
    private val allowed = HashSet<String>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        allowed.addAll(BridgePrefs.allowedApps(this))
        val launcher = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        rows = packageManager.queryIntentActivities(launcher, 0)
            .filter { it.activityInfo.packageName != packageName }
            .map { Row(it.activityInfo.packageName, it.loadLabel(packageManager).toString(), it) }
            .distinctBy { it.pkg }
            .sortedBy { it.label.lowercase() }
        val pad = (16 * resources.displayMetrics.density).toInt()
        val column = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(pad, pad, pad, 0) }
        column.addView(TextView(this).apply { text = getString(R.string.bridge_apps_title); textSize = 24f })
        val adapter = Adapter()
        val list = ListView(this).apply { this.adapter = adapter }
        column.addView(Switch(this).apply {
            text = getString(R.string.bridge_apps_all)
            textSize = 16f
            setPadding(0, pad / 2, 0, pad / 2)
            isChecked = BridgePrefs.allApps(this@BridgeAppsActivity)
            list.alpha = if (isChecked) 0.4f else 1f // the list is not used while "all apps" is on
            setOnCheckedChangeListener { _, on ->
                BridgePrefs.setAllApps(this@BridgeAppsActivity, on)
                list.alpha = if (on) 0.4f else 1f
                adapter.notifyDataSetChanged() // the rows' switches are disabled (and read so by a screen reader) while all apps is on
            }
        })
        column.addView(list, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
        setContentView(column)
    }

    private inner class Adapter : BaseAdapter() {
        override fun getCount() = rows.size
        override fun getItem(position: Int) = rows[position]
        override fun getItemId(position: Int) = position.toLong()

        override fun getView(position: Int, convertView: View?, parent: ViewGroup): View {
            val row = rows[position]
            val line = (convertView as? LinearLayout) ?: LinearLayout(this@BridgeAppsActivity).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = android.view.Gravity.CENTER_VERTICAL
                val pad = (8 * resources.displayMetrics.density).toInt()
                setPadding(0, pad, 0, pad)
                addView(ImageView(context).apply { id = R.id.bridge_app_icon }, LinearLayout.LayoutParams((40 * resources.displayMetrics.density).toInt(), (40 * resources.displayMetrics.density).toInt()))
                addView(TextView(context).apply { id = R.id.bridge_app_label; textSize = 16f; setPadding(pad * 2, 0, pad, 0) }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
                addView(Switch(context).apply { id = R.id.bridge_app_switch })
            }
            line.findViewById<ImageView>(R.id.bridge_app_icon).setImageDrawable(row.info.loadIcon(packageManager))
            line.findViewById<TextView>(R.id.bridge_app_label).text = row.label
            line.findViewById<Switch>(R.id.bridge_app_switch).apply {
                setOnCheckedChangeListener(null) // a recycled row must not fire for the app it showed before
                isChecked = row.pkg in allowed
                isEnabled = !BridgePrefs.allApps(this@BridgeAppsActivity)
                setOnCheckedChangeListener { _, on ->
                    if (on) allowed.add(row.pkg) else allowed.remove(row.pkg)
                    BridgePrefs.setAllowedApps(this@BridgeAppsActivity, allowed)
                }
            }
            return line
        }
    }
}
