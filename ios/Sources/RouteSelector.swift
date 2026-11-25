import Foundation
import Network
import WebKit

struct RouteEntry: Codable, Hashable {
    var kind: String
    var url: String
    var priority: Int
    var requiresLocalAuth: Bool

    enum CodingKeys: String, CodingKey {
        case kind, url, priority
        case localAuth = "local_auth"
        case requiresLocalAuth = "requires_local_auth"
    }

    init(kind: String, url: String, priority: Int, requiresLocalAuth: Bool = false) {
        self.kind = kind
        self.url = RouteNormalizer.clean(url)
        self.priority = priority
        self.requiresLocalAuth = requiresLocalAuth
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        kind = try c.decodeIfPresent(String.self, forKey: .kind) ?? "relay"
        url = RouteNormalizer.clean(try c.decodeIfPresent(String.self, forKey: .url) ?? "")
        priority = try c.decodeIfPresent(Int.self, forKey: .priority) ?? 50
        let localFlag = try c.decodeIfPresent(Bool.self, forKey: .localAuth)
        let legacyFlag = try c.decodeIfPresent(Bool.self, forKey: .requiresLocalAuth)
        requiresLocalAuth = localFlag ?? legacyFlag ?? false
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(kind, forKey: .kind)
        try c.encode(url, forKey: .url)
        try c.encode(priority, forKey: .priority)
        if requiresLocalAuth {
            try c.encode(true, forKey: .localAuth)
        }
    }

    var isDirect: Bool {
        kind == "lan" || kind == "tailscale"
    }

    var displayLabel: String {
        switch kind {
        case "lan": return "局域网直连"
        case "tailscale": return "Tailscale"
        case "tunnel": return "公网隧道"
        case "own": return "自有中转"
        case "public": return "公共中转"
        default: return "中转连接"
        }
    }
}

struct ComputerRecord: Codable, Identifiable, Hashable {
    var id: String
    var name: String
    var url: String
    var environmentId: String
    var routes: [RouteEntry]

    var displayHost: String {
        guard let parsed = URL(string: url) else { return url }
        if parsed.path.range(of: #"^/c/[a-f0-9]{20}"#, options: .regularExpression) != nil {
            return "公共中转"
        }
        return parsed.host ?? url
    }
}

struct ComputerLiveState {
    var online: Bool = false
    var activeOrigin: String = ""
    var activeRouteLabel: String = ""
    var version: String = ""
    var runningCount: Int = 0
    var confirmCount: Int = 0
    var latencyMs: Int? = nil
    var lastError: String = ""
}

struct PairingLink {
    let url: String
    let password: String
    let name: String

    static func parse(_ raw: String) -> PairingLink? {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, let components = URLComponents(string: trimmed) else { return nil }
        if components.scheme?.lowercased() == "remotecli" {
            let items = components.queryItems ?? []
            let u = items.first(where: { $0.name == "u" })?.value ?? ""
            let p = items.first(where: { $0.name == "p" })?.value ?? ""
            let n = items.first(where: { $0.name == "n" })?.value ?? ""
            let cleaned = RouteNormalizer.clean(u)
            guard !cleaned.isEmpty else { return nil }
            return PairingLink(url: cleaned, password: p, name: n)
        }
        if components.scheme?.lowercased() == "http" || components.scheme?.lowercased() == "https" {
            let fragment = components.fragment ?? ""
            var password = ""
            if fragment.hasPrefix("p=") {
                password = String(fragment.dropFirst(2)).removingPercentEncoding ?? ""
            }
            var base = components
            base.fragment = nil
            base.query = nil
            var path = base.path
            if path.hasSuffix("/console") || path.hasSuffix("/console/") {
                path = path.replacingOccurrences(of: "/console/?$", with: "", options: .regularExpression)
            }
            base.path = path
            let cleaned = RouteNormalizer.clean(base.string ?? "")
            guard !cleaned.isEmpty else { return nil }
            return PairingLink(url: cleaned, password: password, name: components.host ?? "")
        }
        return nil
    }
}

enum RouteNormalizer {
    static func clean(_ raw: String) -> String {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: trimmed),
              let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              let host = url.host?.lowercased(),
              !host.isEmpty else {
            return ""
        }
        if isLoopback(host) { return "" }
        var normalized = trimmed
        while normalized.hasSuffix("/") {
            normalized.removeLast()
        }
        return normalized
    }

    static func isLoopback(_ host: String) -> Bool {
        let h = host.lowercased()
        return h == "localhost" || h == "127.0.0.1" || h == "::1" || h == "[::1]" || h.hasPrefix("127.")
    }
}

/// Manages saved computers, multi-route concurrent selection (500ms direct head-start),
/// identity verification (/.well-known/remote-ai), and automatic WebView cookie bootstrap.
@MainActor
final class ComputerStore: ObservableObject {
    @Published var computers: [ComputerRecord] = []
    @Published var states: [String: ComputerLiveState] = [:]

    private let storageKey = "remotecli.ios.computers.v1"
    private let pathMonitor = NWPathMonitor()
    private let monitorQueue = DispatchQueue(label: "io.github.kangwang42.remotecli.net")
    private var lastPathSignature = ""

    init() {
        load()
        startNetworkMonitor()
    }

    deinit {
        pathMonitor.cancel()
    }

    private func load() {
        guard let data = UserDefaults.standard.data(forKey: storageKey),
              let decoded = try? JSONDecoder().decode([ComputerRecord].self, from: data) else {
            computers = []
            return
        }
        computers = decoded
    }

    private func save() {
        if let data = try? JSONEncoder().encode(computers) {
            UserDefaults.standard.set(data, forKey: storageKey)
        }
    }

    private func startNetworkMonitor() {
        pathMonitor.pathUpdateHandler = { [weak self] path in
            let sig = "\(path.status)-\(path.usesInterfaceType(.wifi))-\(path.usesInterfaceType(.cellular))"
            Task { @MainActor [weak self] in
                guard let self else { return }
                if sig != self.lastPathSignature {
                    self.lastPathSignature = sig
                    await self.refreshAll()
                }
            }
        }
        pathMonitor.start(queue: monitorQueue)
    }

    @discardableResult
    func upsert(name: String, url: String, password: String) -> ComputerRecord? {
        let cleaned = RouteNormalizer.clean(url)
        guard !cleaned.isEmpty else { return nil }
        if let idx = computers.firstIndex(where: { $0.url.caseInsensitiveCompare(cleaned) == .orderedSame }) {
            if !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                computers[idx].name = name.trimmingCharacters(in: .whitespacesAndNewlines)
            }
            if !password.isEmpty {
                KeychainStore.save(password, forKey: "pw:\(computers[idx].id)")
            }
            save()
            let updated = computers[idx]
            Task { await refresh(updated) }
            return updated
        }
        let id = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let fallbackName = URL(string: cleaned)?.host ?? "我的电脑"
        let chosenName = name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? fallbackName : name.trimmingCharacters(in: .whitespacesAndNewlines)
        let record = ComputerRecord(id: id, name: chosenName, url: cleaned, environmentId: "", routes: [])
        computers.append(record)
        if !password.isEmpty {
            KeychainStore.save(password, forKey: "pw:\(id)")
        }
        save()
        Task { await refresh(record) }
        return record
    }

    func rename(_ record: ComputerRecord, to newName: String) {
        let trimmed = newName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, let idx = computers.firstIndex(where: { $0.id == record.id }) else { return }
        computers[idx].name = trimmed
        save()
    }

    func remove(_ record: ComputerRecord) {
        computers.removeAll(where: { $0.id == record.id })
        states.removeValue(forKey: record.id)
        KeychainStore.delete(forKey: "pw:\(record.id)")
        KeychainStore.delete(forKey: "local:\(record.id)")
        save()
    }

    func password(for record: ComputerRecord, isLocalDirect: Bool = false) -> String {
        if isLocalDirect, let local = KeychainStore.load(forKey: "local:\(record.id)"), !local.isEmpty {
            return local
        }
        return KeychainStore.load(forKey: "pw:\(record.id)") ?? ""
    }

    func activeOrigin(for record: ComputerRecord) -> String {
        if let st = states[record.id], !st.activeOrigin.isEmpty {
            return st.activeOrigin
        }
        return record.url
    }

    func refreshAll() async {
        let snapshot = computers
        await withTaskGroup(of: Void.self) { group in
            for comp in snapshot {
                group.addTask { [weak self] in
                    await self?.refresh(comp)
                }
            }
        }
    }

    /// Concurrent multi-route selection with 500ms direct head-start and environment_id verification.
    func selectBestOrigin(for record: ComputerRecord) async -> (origin: String, label: String, latencyMs: Int?, isLocal: Bool) {
        let hasLocalSecret = !(KeychainStore.load(forKey: "local:\(record.id)") ?? "").isEmpty
        var candidates: [RouteEntry] = []
        var seen = Set<String>()
        for r in record.routes where !r.url.isEmpty {
            // Do not pick a local-auth-only direct route until the local direct password has been synced.
            if r.requiresLocalAuth && !hasLocalSecret { continue }
            if seen.insert(r.url.lowercased()).inserted {
                candidates.append(r)
            }
        }
        if seen.insert(record.url.lowercased()).inserted {
            let isPublic = record.url.range(of: #"/c/[a-f0-9]{20}$"#, options: .regularExpression) != nil
            candidates.append(RouteEntry(kind: isPublic ? "public" : "own", url: record.url, priority: 50, requiresLocalAuth: false))
        }
        candidates.sort { $0.priority < $1.priority }

        let direct = candidates.filter { $0.isDirect }
        let remote = candidates.filter { !$0.isDirect }

        // 500ms head-start for low-latency direct routes (LAN / Tailscale)
        if !direct.isEmpty {
            if let winner = await probeFirst(direct, expectedEnvId: record.environmentId, timeoutSeconds: 0.5) {
                return (winner.route.url, winner.route.displayLabel, winner.latencyMs, winner.route.requiresLocalAuth)
            }
        }
        // Race all candidates together with a 3s timeout
        if let winner = await probeFirst(direct + remote, expectedEnvId: record.environmentId, timeoutSeconds: 3.0) {
            return (winner.route.url, winner.route.displayLabel, winner.latencyMs, winner.route.requiresLocalAuth)
        }
        return (record.url, record.displayHost, nil, false)
    }

    private func probeFirst(
        _ routes: [RouteEntry],
        expectedEnvId: String,
        timeoutSeconds: Double
    ) async -> (route: RouteEntry, latencyMs: Int)? {
        guard !routes.isEmpty else { return nil }
        return await withTaskGroup(of: (RouteEntry, Int)?.self) { group in
            for route in routes {
                group.addTask {
                    let started = Date()
                    guard let ok = await Self.probeSingle(route: route, expectedEnvId: expectedEnvId, timeout: timeoutSeconds), ok else {
                        return nil
                    }
                    let ms = max(1, Int(Date().timeIntervalSince(started) * 1000))
                    return (route, ms)
                }
            }
            for await result in group {
                if let hit = result {
                    group.cancelAll()
                    return (hit.0, hit.1)
                }
            }
            return nil
        }
    }

    private nonisolated static func probeSingle(route: RouteEntry, expectedEnvId: String, timeout: Double) async -> Bool? {
        guard let url = URL(string: route.url + "/.well-known/remote-ai") else { return false }
        var req = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        req.httpMethod = "GET"
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = timeout
        config.timeoutIntervalForResource = timeout
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        do {
            let (data, resp) = try await session.data(for: req)
            guard let http = resp as? HTTPURLResponse, http.statusCode == 200 else {
                // Fallback for older relays without /.well-known/remote-ai (only for saved primary route)
                if !route.isDirect {
                    return await probeLegacySession(origin: route.url, timeout: timeout)
                }
                return false
            }
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  (json["service"] as? String) == "remote-ai" else {
                return false
            }
            let reportedEnv = (json["environment_id"] as? String) ?? ""
            if !expectedEnvId.isEmpty && !reportedEnv.isEmpty && reportedEnv != expectedEnvId {
                return false
            }
            return true
        } catch {
            return false
        }
    }

    private nonisolated static func probeLegacySession(origin: String, timeout: Double) async -> Bool {
        guard let url = URL(string: origin + "/api/session") else { return false }
        var req = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = timeout
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        do {
            let (_, resp) = try await session.data(for: req)
            return (resp as? HTTPURLResponse)?.statusCode == 200
        } catch {
            return false
        }
    }

    /// Signs in to the chosen origin and synchronizes session cookies into WKWebsiteDataStore.
    func ensureAuthenticated(origin: String, record: ComputerRecord, isLocal: Bool) async -> String? {
        let pw = password(for: record, isLocalDirect: isLocal)
        guard !pw.isEmpty, let loginUrl = URL(string: origin + "/api/login") else { return nil }
        var req = URLRequest(url: loginUrl, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 6)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try? JSONSerialization.data(withJSONObject: ["password": pw])
        do {
            let (data, resp) = try await URLSession.shared.data(for: req)
            guard let http = resp as? HTTPURLResponse, http.statusCode == 200 else { return nil }
            if let fields = http.allHeaderFields as? [String: String], let url = http.url {
                let cookies = HTTPCookie.cookies(withResponseHeaderFields: fields, for: url)
                let store = WKWebsiteDataStore.default().httpCookieStore
                for cookie in cookies {
                    await store.setCookie(cookie)
                }
            }
            if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let token = json["token"] as? String {
                return token
            }
        } catch {
            return nil
        }
        return nil
    }

    func refresh(_ record: ComputerRecord) async {
        var picked = await selectBestOrigin(for: record)
        var token = await ensureAuthenticated(origin: picked.origin, record: record, isLocal: picked.isLocal)
        if token == nil && picked.origin.caseInsensitiveCompare(record.url) != .orderedSame {
            // Direct route auth failed (e.g. rotated local password or stale LAN host): fall back to primary route.
            picked = (record.url, record.displayHost, nil, false)
            token = await ensureAuthenticated(origin: picked.origin, record: record, isLocal: false)
        }
        var state = states[record.id] ?? ComputerLiveState()
        state.activeOrigin = picked.origin
        state.activeRouteLabel = picked.label
        state.latencyMs = picked.latencyMs

        guard let overviewUrl = URL(string: picked.origin + "/api/terminal") else {
            states[record.id] = state
            return
        }
        var req = URLRequest(url: overviewUrl, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 6)
        if let token {
            req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
        do {
            let (data, resp) = try await URLSession.shared.data(for: req)
            guard let http = resp as? HTTPURLResponse, http.statusCode == 200,
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                state.online = false
                state.lastError = "未登录或连接不可达"
                states[record.id] = state
                return
            }
            let device = json["device"] as? [String: Any] ?? [:]
            state.online = (device["online"] as? Bool) ?? false
            state.version = (device["version"] as? String) ?? ""
            state.lastError = ""

            let terms = json["terminals"] as? [[String: Any]] ?? []
            let sess = json["sessions"] as? [[String: Any]] ?? []
            let runningTerms = terms.filter { ($0["state"] as? String) == "running" || ($0["state"] as? String) == "starting" }
            let confirmTerms = runningTerms.filter { ($0["phase"] as? String) == "confirm" || ($0["status"] as? String) == "confirm" }
            let confirmSess = sess.filter { ($0["live"] as? Bool) == true && (($0["status"] as? String) == "confirm" || $0["ask"] != nil) }
            state.runningCount = runningTerms.count
            state.confirmCount = confirmTerms.count + confirmSess.count
            states[record.id] = state

            // Learn environment_id and multi-route table from device snapshot
            if let idx = computers.firstIndex(where: { $0.id == record.id }) {
                var changed = false
                if let envId = device["environment_id"] as? String, !envId.isEmpty, computers[idx].environmentId != envId {
                    computers[idx].environmentId = envId
                    changed = true
                }
                if let rawRoutes = device["routes"] as? [[String: Any]] {
                    var parsed: [RouteEntry] = []
                    for item in rawRoutes {
                        if let u = item["url"] as? String {
                            let clean = RouteNormalizer.clean(u)
                            if !clean.isEmpty {
                                let localAuthFlag = (item["local_auth"] as? Bool) ?? (item["requires_local_auth"] as? Bool) ?? false
                                parsed.append(RouteEntry(
                                    kind: (item["kind"] as? String) ?? "relay",
                                    url: clean,
                                    priority: (item["priority"] as? Int) ?? 50,
                                    requiresLocalAuth: localAuthFlag
                                ))
                            }
                        }
                    }
                    if !parsed.isEmpty && parsed != computers[idx].routes {
                        computers[idx].routes = parsed
                        changed = true
                    }
                }
                if changed { save() }
            }

            // Also fetch local direct password if routes include local_auth and we don't have it yet
            if let token, KeychainStore.load(forKey: "local:\(record.id)") == nil {
                await fetchLocalRouteSecret(origin: picked.origin, token: token, recordId: record.id)
            }
        } catch {
            state.online = false
            state.lastError = "无法连接电脑"
            states[record.id] = state
        }
    }

    private func fetchLocalRouteSecret(origin: String, token: String, recordId: String) async {
        guard let url = URL(string: origin + "/api/computer") else { return }
        var req = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 8)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        let opId = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        req.httpBody = try? JSONSerialization.data(withJSONObject: ["id": opId, "action": "routes_read"])
        do {
            let (data, resp) = try await URLSession.shared.data(for: req)
            guard (resp as? HTTPURLResponse)?.statusCode == 200,
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let localPw = json["local_password"] as? String, !localPw.isEmpty else {
                return
            }
            KeychainStore.save(localPw, forKey: "local:\(recordId)")
        } catch {
            // Optional best-effort sync
        }
    }
}
