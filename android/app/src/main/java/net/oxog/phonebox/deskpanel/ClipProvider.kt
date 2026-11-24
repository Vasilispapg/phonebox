package net.oxog.phonebox.deskpanel

import android.content.ContentProvider
import android.content.ContentValues
import android.database.Cursor
import android.net.Uri
import android.os.Binder
import android.os.Bundle

/**
 * The helper's way to the app: `content call` run by the helper as the shell user (adb's user). Only calls from the shell user
 * are answered (the binder caller's uid, which an app cannot fake); every other caller gets nothing. The answer says whether the
 * app still wants copies ("active") or not ("off", the helper then leaves). A copy is handed to [ClipRelay] and never kept here.
 * Nothing is logged: the text is not in the system's broadcast history or any log, as it would be in a broadcast.
 */
class ClipProvider : ContentProvider() {
    override fun onCreate(): Boolean = true

    override fun call(method: String, arg: String?, extras: Bundle?): Bundle? {
        if (!ClipHelperRules.callerAllowed(Binder.getCallingUid())) return null
        val app = context?.applicationContext ?: return null
        val state = ClipHelperRules.stateFor(ClipRelay.sink != null, BridgePrefs.clipboardEnabled(app), ClipRelay.macOff)
        ClipHelperRules.copyToRelay(state, method, arg)?.let(ClipRelay::submit)
        return Bundle().apply { putString("state", state) }
    }

    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, selectionArgs: Array<out String>?, sortOrder: String?): Cursor? = null
    override fun getType(uri: Uri): String? = null
    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = 0
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?): Int = 0
}
