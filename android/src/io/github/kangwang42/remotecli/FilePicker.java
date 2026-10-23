package io.github.kangwang42.remotecli;

import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ProviderInfo;
import android.net.Uri;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.webkit.WebView;
import android.widget.Toast;
import java.util.ArrayList;

/** Grants a file input only the documents explicitly picked by the user. No storage permission is needed. */
final class FilePicker extends WebChromeClient {
    static final int REQUEST = 4106;
    private final Activity activity;
    private ValueCallback<Uri[]> pending;
    private WebView owner;
    private String page;

    FilePicker(Activity activity) { this.activity = activity; }

    @Override public boolean onShowFileChooser(WebView view, ValueCallback<Uri[]> callback, FileChooserParams params) {
        if (pending != null) { callback.onReceiveValue(null); return true; }
        if (params.getMode() != FileChooserParams.MODE_OPEN && params.getMode() != FileChooserParams.MODE_OPEN_MULTIPLE) {
            callback.onReceiveValue(null); return true;
        }
        pending = callback; owner = view; page = view.getUrl();
        Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*")
                .putExtra(Intent.EXTRA_ALLOW_MULTIPLE, params.getMode() == FileChooserParams.MODE_OPEN_MULTIPLE)
                .putExtra(Intent.EXTRA_LOCAL_ONLY, true).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        try { activity.startActivityForResult(intent, REQUEST); }
        catch (android.content.ActivityNotFoundException | SecurityException unavailable) {
            cancel();
            Toast.makeText(activity, "无法打开系统文件选择器", Toast.LENGTH_LONG).show();
        }
        return true;
    }

    void result(int result, Intent data) {
        if (pending == null) return;
        Uri[] chosen = null;
        if (result == Activity.RESULT_OK && data != null && owner != null && page != null && page.equals(owner.getUrl())) {
            ArrayList<Uri> list = new ArrayList<>();
            if (data.getClipData() != null) {
                for (int i = 0; i < data.getClipData().getItemCount(); i++) list.add(data.getClipData().getItemAt(i).getUri());
            } else if (data.getData() != null) list.add(data.getData());
            boolean valid = !list.isEmpty();
            for (Uri uri : list) {
                if (uri == null || !"content".equals(uri.getScheme())) { valid = false; break; }
                ProviderInfo provider = activity.getPackageManager().resolveContentProvider(uri.getAuthority(), 0);
                if (provider == null || activity.getPackageName().equals(provider.packageName)
                        || activity.checkUriPermission(uri, android.os.Process.myPid(), android.os.Process.myUid(), Intent.FLAG_GRANT_READ_URI_PERMISSION) != PackageManager.PERMISSION_GRANTED) {
                    valid = false; break;
                }
            }
            if (valid) chosen = list.toArray(new Uri[0]);
            else Toast.makeText(activity, "无法读取所选文件，请从系统文件选择器重新选择", Toast.LENGTH_LONG).show();
        }
        ValueCallback<Uri[]> callback = pending;
        pending = null; owner = null; page = null;
        callback.onReceiveValue(chosen);
    }

    void cancel() {
        ValueCallback<Uri[]> callback = pending;
        pending = null; owner = null; page = null;
        if (callback != null) callback.onReceiveValue(null);
    }
}
