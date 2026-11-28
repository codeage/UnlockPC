import SwiftUI
import WatchConnectivity

@main
struct UnlockPCApp: App {
    @StateObject private var push = WatchPushExport()

    var body: some Scene {
        WindowGroup {
            NavigationStack {
                List {
                    Section("Apple Watch") {
                        Text("在 Apple Watch 上打开 UnlockPC，选择测试电脑并设置提醒。")
                        Text("手表通过蓝牙直接接收提醒。")
                            .foregroundStyle(.secondary)
                    }
                    Section("APNs 实测") {
                        Text(push.status)
                    }
                }
                .navigationTitle("UnlockPC")
            }
            .task { push.start() }
        }
    }
}

@MainActor
private final class WatchPushExport: NSObject, ObservableObject, WCSessionDelegate {
    @Published private(set) var status = "等待手表注册 APNs"

    func start() {
        guard WCSession.isSupported() else { return }
        WCSession.default.delegate = self
        WCSession.default.activate()
    }

    nonisolated func session(_ session: WCSession, activationDidCompleteWith activationState: WCSessionActivationState, error: (any Error)?) {
        if activationState == .activated {
            receive(session.receivedApplicationContext)
        } else if let error {
            let message = error.localizedDescription
            Task { @MainActor in status = "手表连接失败：\(message)" }
        }
    }

    nonisolated func sessionDidBecomeInactive(_ session: WCSession) {}

    nonisolated func sessionDidDeactivate(_ session: WCSession) {
        session.activate()
    }

    nonisolated func session(_ session: WCSession, didReceiveApplicationContext applicationContext: [String: Any]) {
        receive(applicationContext)
    }

    nonisolated func session(_ session: WCSession, didReceiveUserInfo userInfo: [String: Any]) {
        guard let deliveries = userInfo["unlockpcWatchPushDeliveries"] as? String else { return }
        Task { @MainActor in
            do {
                let file = URL.documentsDirectory.appending(path: "UnlockPC-Watch-APNs-Deliveries.csv")
                try Data(deliveries.utf8).write(to: file, options: [.atomic, .completeFileProtection])
                status = "手表通知送达记录已就绪"
            } catch {
                status = "送达记录导出失败：\(error.localizedDescription)"
            }
        }
    }

    nonisolated func session(_ session: WCSession, didReceive file: WCSessionFile) {
        guard file.metadata?["unlockpcWatchBatteryLog"] != nil else { return }
        // The received file is deleted when this method returns, so copy it synchronously.
        let message: String
        do {
            let data = try Data(contentsOf: file.fileURL)
            let target = URL.documentsDirectory.appending(path: "UnlockPC-Watch-Battery.csv")
            try data.write(to: target, options: [.atomic, .completeFileProtection])
            message = "手表电量记录已就绪"
        } catch {
            message = "电量记录保存失败：\(error.localizedDescription)"
        }
        Task { @MainActor in status = message }
    }

    nonisolated private func receive(_ context: [String: Any]) {
        guard let token = context["unlockpcWatchPushToken"] as? String,
              !token.isEmpty, token.count.isMultiple(of: 2),
              token.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) })
        else { return }

        Task { @MainActor in
            do {
                let file = URL.documentsDirectory.appending(path: "UnlockPC-Watch-APNs.txt")
                try Data(token.utf8).write(to: file, options: [.atomic, .completeFileProtection])
                status = "手表 APNs 已就绪，可由已信任的电脑读取"
            } catch {
                status = "令牌导出失败：\(error.localizedDescription)"
            }
        }
    }
}
