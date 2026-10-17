package io.github.kangwang42.remotecli;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import org.json.JSONObject;

/** Bindings never leave this phone. Ciphertext is tied to the saved computer record. */
final class DesktopStore {
    private static final String ALIAS = "remote-ai-uu-bindings";
    private final SharedPreferences prefs;
    DesktopStore(Context context) { prefs = context.getSharedPreferences("remote-ai-desktop", Context.MODE_PRIVATE); }
    private SecretKey key() throws Exception {
        KeyStore keys = KeyStore.getInstance("AndroidKeyStore"); keys.load(null);
        if (!keys.containsAlias(ALIAS)) {
            KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build());
            generator.generateKey();
        }
        return (SecretKey) keys.getKey(ALIAS, null);
    }
    boolean bound(String record) { return prefs.contains("binding-" + record); }
    void save(String record, String device) throws Exception {
        if (!DesktopProtocol.validId(record) || !DesktopProtocol.validDevice(device)) throw new IllegalArgumentException();
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.ENCRYPT_MODE, key());
        cipher.updateAAD(record.getBytes(StandardCharsets.UTF_8));
        String value = Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP) + ":" + Base64.encodeToString(cipher.doFinal(device.getBytes(StandardCharsets.UTF_8)), Base64.NO_WRAP);
        if (!prefs.edit().putString("binding-" + record, value).commit()) throw new IllegalStateException();
    }
    String read(String record) throws Exception {
        String[] parts = prefs.getString("binding-" + record, "").split(":", -1);
        if (parts.length != 2) throw new IllegalArgumentException();
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(128, Base64.decode(parts[0], Base64.NO_WRAP)));
        cipher.updateAAD(record.getBytes(StandardCharsets.UTF_8));
        return new String(cipher.doFinal(Base64.decode(parts[1], Base64.NO_WRAP)), StandardCharsets.UTF_8);
    }
    void remove(String record) {
        SharedPreferences.Editor edit = prefs.edit().remove("binding-" + record).remove("notice-" + record);
        if (record.equals(pending().optString("record"))) edit.remove("pending");
        edit.commit();
    }
    JSONObject pending() {
        try { return new JSONObject(prefs.getString("pending", "{}")); } catch (Exception invalid) { return new JSONObject(); }
    }
    boolean begin(String record, String operation, String nonce) {
        try { return prefs.edit().putString("pending", new JSONObject().put("record", record).put("operation", operation)
            .put("nonce", nonce).put("created", System.currentTimeMillis()).toString()).commit(); } catch (Exception invalid) { return false; }
    }
    boolean clearPending() { return prefs.edit().remove("pending").commit(); }
    String notice(String record) { return prefs.getString("notice-" + record, ""); }
    void notice(String record, String text) { prefs.edit().putString("notice-" + record, text).apply(); }
}
