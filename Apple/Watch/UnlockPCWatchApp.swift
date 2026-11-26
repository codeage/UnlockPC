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
                    Button(alerts.isScanning ? "停止搜索" : "搜索电脑") {
                        alerts.toggleScan()
                    }
                }
                if alerts.candidates.count > 1 || (!alerts.isScanning && !alerts.candidates.isEmpty) {
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
        .onAppear { alerts.setForeground(scenePhase == .active) }
        .onChange(of: scenePhase) { alerts.setForeground($0 == .active) }
    }
}
