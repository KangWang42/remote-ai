package io.github.kangwang42.remotecli;

import android.app.Activity;
import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.provider.MediaStore;
import android.util.Base64;
import android.webkit.MimeTypeMap;
import java.io.File;
import java.io.FileOutputStream;
import java.io.OutputStream;

/**
 * Keeps a file the page hands over, piece by piece, in the phone's Download folder (Download/RemoteAI), where
 * every file manager and every other app finds it. One file at a time. Needs no permission: from Android 10 on
 * the system's own list of downloads is written to; before that, the app's own folder on shared storage.
 */
final class Saver {
    static final long LIMIT = 300L * 1024 * 1024;
    private final Activity activity;
    private OutputStream out;
    private Uri uri, last;
    private File file;
    private String type = "", lastType = "";
    private long written;

    Saver(Activity activity) { this.activity = activity; }

    /** A name that is only a name: nothing that leads into another folder, nothing a file system refuses. */
    static String clean(String name) {
        String only = name == null ? "" : name.replaceAll("[\\\\/:*?\"<>|\\p{Cntrl}]", "_").trim();
        while (only.startsWith(".")) only = only.substring(1);
        if (only.length() > 120) only = only.substring(only.length() - 120);
        return only.isEmpty() ? "file" : only;
    }
    static String typeOf(String name) {
        int dot = name.lastIndexOf('.');
        String known = dot < 0 ? null : MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substring(dot + 1).toLowerCase(java.util.Locale.ROOT));
        return known == null ? "application/octet-stream" : known;
    }

    /** Begins a file. Returns nothing when it went well, else what to tell the user. */
    synchronized String start(String name) {
        cancel();
        String shown = clean(name);
        type = typeOf(shown);
        written = 0;
        try {
            if (Build.VERSION.SDK_INT >= 29) {
                ContentValues values = new ContentValues();
                values.put(MediaStore.MediaColumns.DISPLAY_NAME, shown);
                values.put(MediaStore.MediaColumns.MIME_TYPE, type);
                values.put(MediaStore.MediaColumns.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/RemoteAI");
                values.put(MediaStore.MediaColumns.IS_PENDING, 1);
                ContentResolver resolver = activity.getContentResolver();
                uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
                if (uri == null) return "手机没有接受这个文件";
                out = resolver.openOutputStream(uri);
            } else {
                File folder = activity.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS);
                if (folder == null) return "手机的存储现在不能用";
                file = new File(folder, shown);
                for (int n = 1; file.exists() && n < 1000; n++) file = new File(folder, n + "-" + shown);
                out = new FileOutputStream(file);
            }
            return out == null ? "手机没有接受这个文件" : "";
        } catch (Exception failed) { cancel(); return "没有保存：" + failed.getClass().getSimpleName(); }
    }
    /** One piece, as base64 text. Returns nothing when it went well. */
    synchronized String piece(String data) {
        if (out == null) return "没有正在保存的文件";
        try {
            byte[] bytes = Base64.decode(data, Base64.DEFAULT);
            written += bytes.length;
            if (written > LIMIT) { cancel(); return "文件太大，没有保存"; }
            out.write(bytes);
            return "";
        } catch (Exception failed) { cancel(); return "没有保存：手机的存储空间可能不够"; }
    }
    /** Ends the file. Returns where it is, for the user to read, or nothing when it failed. */
    synchronized String finish() {
        if (out == null) return "";
        try {
            out.close(); out = null;
            if (uri != null) {
                ContentValues values = new ContentValues();
                values.put(MediaStore.MediaColumns.IS_PENDING, 0);
                activity.getContentResolver().update(uri, values, null, null);
                last = uri; lastType = type; uri = null;
                return "下载 / RemoteAI";
            }
            String where = file.getAbsolutePath();
            last = null; file = null;
            return where;
        } catch (Exception failed) { cancel(); return ""; }
    }
    /** Gives up the file that was begun and removes what there is of it. */
    synchronized void cancel() {
        try { if (out != null) out.close(); } catch (Exception ignored) { /* it is removed below */ }
        out = null;
        try { if (uri != null) activity.getContentResolver().delete(uri, null, null); } catch (Exception ignored) { /* nothing was kept */ }
        try { if (file != null) file.delete(); } catch (Exception ignored) { /* nothing was kept */ }
        uri = null; file = null;
    }
    /** Opens the file saved last with an app of the phone that reads its kind. False when there is none. */
    boolean open() {
        final Uri saved; final String kind;
        synchronized (this) { saved = last; kind = lastType; }
        if (saved == null) return false;
        try {
            Intent view = new Intent(Intent.ACTION_VIEW).setDataAndType(saved, kind).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            activity.startActivity(Intent.createChooser(view, "用哪个应用打开"));
            return true;
        } catch (Exception none) { return false; }
    }
}
