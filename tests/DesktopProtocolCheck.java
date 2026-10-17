package io.github.kangwang42.remotecli;

import java.net.URI;
import java.util.Map;

public final class DesktopProtocolCheck {
    private static int checked;
    private static void check(boolean value) { checked++; if (!value) throw new AssertionError("Case " + checked); }
    public static void main(String[] args) throws Exception {
        String nonce = DesktopProtocol.id(), device = "opaque+id/=中文", callback = DesktopProtocol.callback("bind", nonce);
        long now = 1000000;
        String success = callback + "&nonce=" + nonce + "&anonymous_device_id=" + DesktopProtocol.encode(device);
        DesktopProtocol.Result result = DesktopProtocol.receive(success, "bind", nonce, now, now);
        check(result != null && device.equals(result.device));
        Map<String, String> request = DesktopProtocol.query(new URI(DesktopProtocol.bind(nonce)));
        check(callback.equals(request.get("callback")) && nonce.equals(request.get("nonce")));
        check(DesktopProtocol.receive(success, "bind", DesktopProtocol.id(), now, now) == null);
        check(DesktopProtocol.receive(success, "bind", nonce, now, now + DesktopProtocol.REQUEST_LIFETIME + 1) == null);
        check(DesktopProtocol.receive(success, "bind", nonce, now, now - 1) == null);
        check(DesktopProtocol.receive(success + "&nonce=" + nonce, "bind", nonce, now, now) == null);
        check(DesktopProtocol.receive(success + "&error=user_cancel", "bind", nonce, now, now) == null);
        check(DesktopProtocol.receive(success.replace("result/", "evil/"), "bind", nonce, now, now) == null);
        check(DesktopProtocol.receive(success.replace("result/", "other@result/"), "bind", nonce, now, now) == null);
        check(DesktopProtocol.receive(success + "#extra", "bind", nonce, now, now) == null);
        check(DesktopProtocol.receive(success.replace("nonce=", "bad="), "bind", nonce, now, now) == null);
        check(DesktopProtocol.receive(callback + "&nonce=" + nonce + "&error=user_cancel", "bind", nonce, now, now).error.equals("user_cancel"));
        String control = DesktopProtocol.callback("control", nonce);
        check(DesktopProtocol.receive(control + "&status=success", "control", nonce, now, now) != null);
        check(DesktopProtocol.receive(control + "&status=success&reason=old", "control", nonce, now, now) == null);
        check(DesktopProtocol.receive(control + "&status=failure", "control", nonce, now, now) == null);
        check(DesktopProtocol.receive(control + "&status=failure&reason=pip_unsupported", "control", nonce, now, now).error.equals("pip_unsupported"));
        check(DesktopProtocol.receive(control + "&status=failure&reason=future_code", "control", nonce, now, now) != null);
        check(DesktopProtocol.receive(control + "&success=1", "control", nonce, now, now) == null);
        check(DesktopProtocol.receive(success, "control", nonce, now, now) == null);
        check(!DesktopProtocol.control(device, false, nonce).contains("callback="));
        Map<String, String> small = DesktopProtocol.query(new URI(DesktopProtocol.control(device, true, nonce)));
        check(device.equals(small.get("anonymous_device_id")) && control.equals(small.get("callback")));
        check(!DesktopProtocol.validDevice("line\nsecret"));
        check(!DesktopProtocol.validDevice(""));
        check(DesktopProtocol.receive(success, "bind", "", now, now) == null);
        check(DesktopProtocol.receive(success.replace("request=", "request=%ZZ"), "bind", nonce, now, now) == null);
        String first = DesktopProtocol.id(), second = DesktopProtocol.id();
        String[] ids = {first, second}, urls = {"https://old.example", "https://other.example"};
        check(ComputerIdentity.match(ids, urls, "https://old.example", "") == 0);
        check(ComputerIdentity.match(ids, urls, "https://new.example", "") == -1);
        check(ComputerIdentity.match(ids, urls, "https://new.example", first) == 0);
        check(ComputerIdentity.match(ids, urls, "https://new.example", second) == 1);
        check(ComputerIdentity.match(ids, urls, "https://other.example", first) == 1);
        check(ComputerIdentity.match(ids, urls, "https://new.example", DesktopProtocol.id()) == -1);
        System.out.println("Desktop protocol: " + checked + " checks passed");
    }
}
