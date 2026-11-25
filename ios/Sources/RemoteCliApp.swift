import SwiftUI

@main
struct RemoteCliApp: App {
    @StateObject private var store = ComputerStore()

    var body: some Scene {
        WindowGroup {
            HomeView(store: store)
                .onOpenURL { url in
                    if let parsed = PairingLink.parse(url.absoluteString) {
                        store.upsert(name: parsed.name, url: parsed.url, password: parsed.password)
                    }
                }
        }
    }
}

struct ActiveSessionTarget: Identifiable {
    let id = UUID()
    let computer: ComputerRecord
    let origin: String
    let path: String
    let password: String
}

struct HomeView: View {
    @ObservedObject var store: ComputerStore
    @State private var showScanner = false
    @State private var showManualAdd = false
    @State private var manualName = ""
    @State private var manualUrl = ""
    @State private var manualPassword = ""
    @State private var addError = ""
    @State private var activeTarget: ActiveSessionTarget?
    @State private var openingId: String?
    @State private var chromeColor: Color = Color(uiColor: .systemBackground)

    var body: some View {
        NavigationStack {
            Group {
                if store.computers.isEmpty {
                    emptyState
                } else {
                    List {
                        Section {
                            ForEach(store.computers) { computer in
                                ComputerRow(
                                    computer: computer,
                                    state: store.states[computer.id],
                                    opening: openingId == computer.id,
                                    onOpenConsole: { open(computer, path: "/console/") },
                                    onOpenClassic: { open(computer, path: "/") }
                                )
                                .swipeActions(edge: .trailing) {
                                    Button(role: .destructive) {
                                        store.remove(computer)
                                    } label: {
                                        Label("移除", systemImage: "trash")
                                    }
                                }
                            }
                        } header: {
                            Text("我的电脑")
                        } footer: {
                            Text("点击电脑直接进入统一总控台（支持 Diff 审查、Worktree 隔离、多 AI 工具与动态主题）；长按可切换经典移动视图。")
                        }
                    }
                    .listStyle(.insetGrouped)
                    .refreshable {
                        await store.refreshAll()
                    }
                }
            }
            .navigationTitle("Remote AI")
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    Button {
                        Task { await store.refreshAll() }
                    } label: {
                        Image(systemName: "arrow.clockwise")
                    }
                    .accessibilityLabel("刷新状态")
                }
                ToolbarItem(placement: .navigationBarTrailing) {
                    Menu {
                        Button {
                            showScanner = true
                        } label: {
                            Label("扫码连接电脑", systemImage: "qrcode.viewfinder")
                        }
                        Button {
                            addError = ""
                            showManualAdd = true
                        } label: {
                            Label("手动输入地址", systemImage: "keyboard")
                        }
                    } label: {
                        Image(systemName: "plus.circle.fill")
                            .font(.title3)
                    }
                }
            }
            .task {
                await store.refreshAll()
            }
            .fullScreenCover(isPresented: $showScanner) {
                ScannerView(
                    onCode: { raw in
                        showScanner = false
                        if let parsed = PairingLink.parse(raw),
                           let saved = store.upsert(name: parsed.name, url: parsed.url, password: parsed.password) {
                            open(saved, path: "/console/")
                        } else {
                            addError = "未识别到有效的 Remote AI 连接二维码"
                            showManualAdd = true
                        }
                    },
                    onCancel: {
                        showScanner = false
                    }
                )
                .ignoresSafeArea()
            }
            .sheet(isPresented: $showManualAdd) {
                manualAddSheet
            }
            .fullScreenCover(item: $activeTarget) { target in
                ZStack {
                    chromeColor.ignoresSafeArea()
                    ConsoleWebView(
                        origin: target.origin,
                        path: target.path,
                        computerName: target.computer.name,
                        password: target.password,
                        onHome: {
                            activeTarget = nil
                            Task { await store.refreshAll() }
                        },
                        onThemeColor: { uiColor in
                            chromeColor = Color(uiColor: uiColor)
                        }
                    )
                    .ignoresSafeArea(.container, edges: .bottom)
                }
            }
        }
    }

    private var emptyState: some View {
        VStack(spacing: 18) {
            Image(systemName: "desktopcomputer")
                .font(.system(size: 52))
                .foregroundStyle(.tint)
            Text("连接你的电脑")
                .font(.title2.bold())
            Text("在电脑上打开 Remote AI，点击“扫码”按钮，用 iPhone 扫描二维码即可自动多路由直连并打开总控台。")
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 28)
            VStack(spacing: 10) {
                Button {
                    showScanner = true
                } label: {
                    Label("扫码连接电脑", systemImage: "qrcode.viewfinder")
                        .frame(maxWidth: 260)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)

                Button {
                    addError = ""
                    showManualAdd = true
                } label: {
                    Text("手动输入地址与密码")
                        .frame(maxWidth: 260)
                }
                .buttonStyle(.bordered)
                .controlSize(.large)
            }
            .padding(.top, 6)
        }
        .padding()
    }

    private var manualAddSheet: some View {
        NavigationStack {
            Form {
                Section("连接信息") {
                    TextField("名称（可选，例如 工作台电脑）", text: $manualName)
                    TextField("地址（例如 https://... 或 http://192.168.1.10:8722）", text: $manualUrl)
                        .keyboardType(.URL)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                    SecureField("访问密码", text: $manualPassword)
                }
                if !addError.isEmpty {
                    Section {
                        Text(addError)
                            .foregroundStyle(.red)
                            .font(.footnote)
                    }
                }
            }
            .navigationTitle("添加电脑")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("取消") { showManualAdd = false }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("连接") {
                        let cleaned = RouteNormalizer.clean(manualUrl)
                        guard !cleaned.isEmpty else {
                            addError = "请输入有效的 http:// 或 https:// 地址（不能使用 127.0.0.1）"
                            return
                        }
                        if let saved = store.upsert(name: manualName, url: cleaned, password: manualPassword) {
                            manualName = ""
                            manualUrl = ""
                            manualPassword = ""
                            showManualAdd = false
                            open(saved, path: "/console/")
                        }
                    }
                    .disabled(manualUrl.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
        }
    }

    private func open(_ computer: ComputerRecord, path: String) {
        openingId = computer.id
        Task {
            var picked = await store.selectBestOrigin(for: computer)
            let token = await store.ensureAuthenticated(origin: picked.origin, record: computer, isLocal: picked.isLocal)
            if token == nil && picked.origin.caseInsensitiveCompare(computer.url) != .orderedSame {
                picked = (computer.url, computer.displayHost, nil, false)
                _ = await store.ensureAuthenticated(origin: picked.origin, record: computer, isLocal: false)
            }
            let pw = store.password(for: computer, isLocalDirect: picked.isLocal)
            openingId = nil
            activeTarget = ActiveSessionTarget(computer: computer, origin: picked.origin, path: path, password: pw)
        }
    }
}

private struct ComputerRow: View {
    let computer: ComputerRecord
    let state: ComputerLiveState?
    let opening: Bool
    let onOpenConsole: () -> Void
    let onOpenClassic: () -> Void

    var body: some View {
        Button(action: onOpenConsole) {
            HStack(spacing: 12) {
                Circle()
                    .fill((state?.online ?? false) ? Color.green : Color.secondary.opacity(0.4))
                    .frame(width: 10, height: 10)
                VStack(alignment: .leading, spacing: 3) {
                    HStack(spacing: 6) {
                        Text(computer.name)
                            .font(.headline)
                            .foregroundStyle(.primary)
                        if let v = state?.version, !v.isEmpty {
                            Text("v\(v)")
                                .font(.caption2)
                                .padding(.horizontal, 6)
                                .padding(.vertical, 2)
                                .background(Color.secondary.opacity(0.14), in: Capsule())
                                .foregroundStyle(.secondary)
                        }
                    }
                    HStack(spacing: 6) {
                        Text(state?.activeRouteLabel.isEmpty == false ? state!.activeRouteLabel : computer.displayHost)
                        if let ms = state?.latencyMs {
                            Text("· \(ms)ms")
                        }
                        if let running = state?.runningCount, running > 0 {
                            Text("· \(running) 运行中")
                                .foregroundStyle(.blue)
                        }
                        if let confirm = state?.confirmCount, confirm > 0 {
                            Text("· \(confirm) 待确认")
                                .foregroundStyle(.orange)
                        }
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)
                }
                Spacer()
                if opening {
                    ProgressView()
                } else {
                    Image(systemName: "chevron.right")
                        .font(.caption.bold())
                        .foregroundStyle(.tertiary)
                }
            }
            .padding(.vertical, 4)
        }
        .contextMenu {
            Button("打开总控台", action: onOpenConsole)
            Button("打开经典移动视图", action: onOpenClassic)
        }
    }
}
