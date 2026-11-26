import SwiftUI
import UserNotifications
import WatchConnectivity
import WatchKit

@main
@MainActor
struct UnlockPCWatchApp: App {
    @WKApplicationDelegateAdaptor private var push: WatchPushTest
    @StateObject private var alerts = TestAlerts()

    var body: some Scene {
        WindowGroup {
            WatchHomeView(alerts: alerts, push: push)
                .task { await push.register() }
        }
        .backgroundTask(.bluetoothAlert) {
            await alerts.handleBackgroundAlert()
        }
    }
}

private struct WatchHomeView: View {
    @Environment(\.scenePhase) private var scenePhase
    @ObservedObject var alerts: TestAlerts
    @ObservedObject var push: WatchPushTest

    var body: some View {
        NavigationStack {
            List {
                Section("APNs 实测") {
                    Text(push.status)
                    Button("注册推送") {
                        Task { await push.register() }
                    }
                    Button("导出送达记录") {
                        Task { await push.exportDeliveries() }
                    }
                }
                Section("蓝牙测试提醒") {
                    Text(alerts.status)
                    if let received = alerts.lastReceived {
                        LabeledContent("最近收包") { Text(received, style: .time) }
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

@MainActor
private final class WatchPushTest: NSObject, ObservableObject, WKApplicationDelegate, WCSessionDelegate {
    @Published private(set) var status = "点按注册，准备 Windows 解锁实测"
    private var pendingToken: String?

    func register() async {
        do {
            let allowed = try await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound])
            guard allowed else {
                status = "请在系统设置中允许通知"
                return
            }
            guard WCSession.isSupported() else {
                status = "无法连接配对的 iPhone"
                return
            }
            let session = WCSession.default
            session.delegate = self
            session.activate()
            status = "正在注册 APNs"
            WKApplication.shared().registerForRemoteNotifications()
        } catch {
            status = "通知授权失败：\(error.localizedDescription)"
        }
    }

    func didRegisterForRemoteNotifications(withDeviceToken deviceToken: Data) {
        pendingToken = deviceToken.map { String(format: "%02x", $0) }.joined()
        forwardToken()
    }

    func exportDeliveries() async {
        let session = WCSession.default
        guard session.activationState == .activated else {
            status = "请先注册推送"
            return
        }
        let notifications = await UNUserNotificationCenter.current().deliveredNotifications()
        let lines = notifications.compactMap { notification -> String? in
            guard notification.request.trigger is UNPushNotificationTrigger,
                  let event = notification.request.content.userInfo["unlockpcTestEvent"] as? String,
                  UUID(uuidString: event) != nil
            else { return nil }

            let milliseconds = Int64(notification.date.timeIntervalSince1970 * 1000)
            return "\(event),\(milliseconds)"
        }
        session.transferUserInfo(["unlockpcWatchPushDeliveries": lines.joined(separator: "\n")])
        status = "已开始同步 \(lines.count) 条送达记录"
    }

    func didFailToRegisterForRemoteNotificationsWithError(_ error: any Error) {
        status = "APNs 注册失败：\(error.localizedDescription)"
    }

    nonisolated func session(_ session: WCSession, activationDidCompleteWith activationState: WCSessionActivationState, error: (any Error)?) {
        let failure = error?.localizedDescription
        Task { @MainActor in
            if let failure {
                status = "配对连接失败：\(failure)"
            } else {
                forwardToken()
            }
        }
    }

    private func forwardToken() {
        let session = WCSession.default
        guard let pendingToken, session.activationState == .activated else { return }
        do {
            try session.updateApplicationContext(["unlockpcWatchPushToken": pendingToken])
            self.pendingToken = nil
            status = "APNs 已注册，令牌正在同步到 iPhone"
        } catch {
            status = "令牌同步失败：\(error.localizedDescription)"
        }
    }
}
