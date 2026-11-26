import SwiftUI

@main
@MainActor
struct UnlockPCWatchApp: App {
    @StateObject private var alerts = TestAlerts()

    var body: some Scene {
        WindowGroup {
            WatchHomeView(alerts: alerts)
                .task { await alerts.requestPermission(onlyIfUndetermined: true) }
        }
        .backgroundTask(.bluetoothAlert) {
            await alerts.handleBackgroundAlert()
        }
    }
}

private struct WatchHomeView: View {
    @Environment(\.scenePhase) private var scenePhase
    @ObservedObject var alerts: TestAlerts
    @State private var code = ""

    var body: some View {
        NavigationStack {
            List {
                Section("蓝牙测试提醒") {
                    Text(alerts.status)
                    if let received = alerts.lastReceived {
                        LabeledContent("最近收包") { Text(received, style: .time) }
                    }
                    Button("允许通知") {
                        Task { await alerts.requestPermission() }
                    }
                    TextField("电脑上的测试码", text: $code, prompt: Text("20 位数字"))
                        .privacySensitive()
                    Button("保存测试码") {
                        if alerts.saveCode(code) {
                            code = ""
                        }
                    }
                    .disabled(code.isEmpty)
                    if alerts.hasCode {
                        Button(alerts.isScanning ? "停止搜索" : "搜索电脑") {
                            alerts.toggleScan()
                        }
                    }
                }
                if !alerts.candidates.isEmpty {
                    Section("选择电脑") {
                        ForEach(alerts.candidates) { candidate in
                            Button(candidate.label) { alerts.select(candidate.id) }
                        }
                    }
                }
                if alerts.hasSelection {
                    Button("忘记测试电脑", role: .destructive) {
                        alerts.forget()
                    }
                }
            }
            .navigationTitle("UnlockPC")
        }
        .onChange(of: scenePhase) { phase in
            if phase == .background {
                alerts.pauseScan()
            }
        }
    }
}
