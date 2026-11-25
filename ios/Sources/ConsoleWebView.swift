import SwiftUI
import UIKit
import WebKit

/// Full-screen WKWebView hosting the unified Remote AI Console (/console/) or classic UI (/)
/// with a native window.RemoteCliNative bridge for haptics, skin sync, and returning to multi-computer list.
struct ConsoleWebView: UIViewRepresentable {
    let origin: String
    let path: String
    let computerName: String
    let password: String
    let onHome: () -> Void
    let onThemeColor: (UIColor) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(computerName: computerName, onHome: onHome, onThemeColor: onThemeColor)
    }

    func makeUIView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        config.websiteDataStore = .default()
        config.allowsInlineMediaPlayback = true
        config.defaultWebpagePreferences.allowsContentJavaScript = true

        let controller = WKUserContentController()
        controller.add(context.coordinator, name: "remotecli")

        let initialSkin = UserDefaults.standard.string(forKey: "rcli.pref.skin") ?? "auto"
        let escapedName = computerName.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "'", with: "\\'")
        let escapedSkin = initialSkin.replacingOccurrences(of: "'", with: "")
        let shim = """
        (function() {
          var prefs = { skin: '\(escapedSkin)' };
          try {
            var stored = localStorage.getItem('rcli-skin');
            if (!prefs.skin && stored) prefs.skin = stored;
          } catch (e) {}
          function send(action, payload) {
            try { window.webkit.messageHandlers.remotecli.postMessage(Object.assign({ action: action }, payload || {})); } catch (e) {}
          }
          window.RemoteCliNative = {
            home: function() { send('home'); },
            disconnect: function() { send('home'); },
            computer: function() { return '\(escapedName)'; },
            haptic: function(kind) { send('haptic', { kind: kind || 'tick' }); },
            chrome: function(color) { send('chrome', { color: color || '' }); },
            openUrl: function(url) { send('openUrl', { url: url || '' }); },
            pref: function(key) { return prefs[key] || ''; },
            setPref: function(key, value) { prefs[key] = String(value || ''); send('setPref', { key: key, value: String(value || '') }); },
            version: function() { return '1.23.0'; },
            canUploadFiles: function() { return true; },
            canDictate: function() { return false; }
          };
        })();
        """
        controller.addUserScript(WKUserScript(source: shim, injectionTime: .atDocumentStart, forMainFrameOnly: false))
        config.userContentController = controller

        let web = WKWebView(frame: .zero, configuration: config)
        web.navigationDelegate = context.coordinator
        web.allowsBackForwardNavigationGestures = false
        web.scrollView.contentInsetAdjustmentBehavior = .never
        web.isOpaque = false
        web.backgroundColor = .systemBackground

        loadTarget(in: web)
        return web
    }

    func updateUIView(_ uiView: WKWebView, context: Context) {
        context.coordinator.onHome = onHome
        context.coordinator.onThemeColor = onThemeColor
    }

    private func loadTarget(in webView: WKWebView) {
        let cleanOrigin = origin.hasSuffix("/") ? String(origin.dropLast()) : origin
        var urlString = cleanOrigin + path
        if !password.isEmpty,
           let encoded = password.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) {
            urlString += "#p=\(encoded)"
        }
        if let url = URL(string: urlString) {
            webView.load(URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData))
        }
    }

    final class Coordinator: NSObject, WKNavigationDelegate, WKScriptMessageHandler {
        let computerName: String
        var onHome: () -> Void
        var onThemeColor: (UIColor) -> Void

        init(computerName: String, onHome: @escaping () -> Void, onThemeColor: @escaping (UIColor) -> Void) {
            self.computerName = computerName
            self.onHome = onHome
            self.onThemeColor = onThemeColor
        }

        func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
            guard let body = message.body as? [String: Any],
                  let action = body["action"] as? String else { return }
            switch action {
            case "home":
                onHome()
            case "haptic":
                let kind = body["kind"] as? String ?? "tick"
                let style: UIImpactFeedbackGenerator.FeedbackStyle = kind == "confirm" ? .medium : .light
                UIImpactFeedbackGenerator(style: style).impactOccurred()
            case "setPref":
                if let key = body["key"] as? String, let val = body["value"] as? String, !key.isEmpty {
                    UserDefaults.standard.set(val, forKey: "rcli.pref.\(key)")
                }
            case "chrome":
                if let hex = body["color"] as? String, let color = UIColor(hexString: hex) {
                    onThemeColor(color)
                }
            case "openUrl":
                if let raw = body["url"] as? String, let url = URL(string: raw),
                   let scheme = url.scheme?.lowercased(), scheme == "http" || scheme == "https" {
                    UIApplication.shared.open(url)
                }
            default:
                break
            }
        }

        func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            guard let url = navigationAction.request.url else {
                decisionHandler(.allow)
                return
            }
            if navigationAction.targetFrame == nil,
               let scheme = url.scheme?.lowercased(), scheme == "http" || scheme == "https" {
                UIApplication.shared.open(url)
                decisionHandler(.cancel)
                return
            }
            decisionHandler(.allow)
        }
    }
}

private extension UIColor {
    convenience init?(hexString: String) {
        let hex = hexString.trimmingCharacters(in: CharacterSet.alphanumerics.inverted)
        guard hex.count == 6, let int = UInt64(hex, radix: 16) else { return nil }
        let r = CGFloat((int >> 16) & 0xFF) / 255.0
        let g = CGFloat((int >> 8) & 0xFF) / 255.0
        let b = CGFloat(int & 0xFF) / 255.0
        self.init(red: r, green: g, blue: b, alpha: 1.0)
    }
}
