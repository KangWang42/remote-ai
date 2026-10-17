package io.github.kangwang42.remotecli;

/** Names are labels, never identity. Only explicit re-pairing can move a saved record. */
final class ComputerIdentity {
    static int match(String[] ids, String[] urls, String url, String selected) {
        for (int i = 0; i < urls.length; i++) if (url.equals(urls[i])) return i;
        if (DesktopProtocol.validId(selected)) for (int i = 0; i < ids.length; i++) if (selected.equals(ids[i])) return i;
        return -1;
    }
}
