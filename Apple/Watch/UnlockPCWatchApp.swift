import SwiftUI
import UserNotifications
import WatchConnectivity
import WatchKit
@preconcurrency import CoreBluetooth
import OSLog

@main
@MainActor
struct UnlockPCWatchApp: App {
    @WKApplicationDelegateAdaptor private var push: WatchPushTest
    @StateObject private var alerts = TestAlerts()
    @StateObject private var broadcast = BroadcastWakeTest()

    var body: some Scene {
        WindowGroup {
            WatchHomeView(alerts: alerts, push: push, broadcast: broadcast)
                .task {
                    let center = UNUserNotificationCenter.current()
                    if await center.notificationSettings().authorizationStatus == .notDetermined {
                        _ = try? await center.requestAuthorization(options: [.alert, .sound])
                    }
                }
        }
        .backgroundTask(.bluetoothAlert) {
            if await broadcast.armed {
                await broadcast.handleBackground()
            } else {
                await alerts.handleBackgroundAlert()
            }
        }
    }
}

private struct WatchHomeView: View {
    @Environment(\.scenePhase) private var scenePhase
    @ObservedObject var alerts: TestAlerts
    @ObservedObject var push: WatchPushTest
    @ObservedObject var broadcast: BroadcastWakeTest

    var body: some View {
        NavigationStack {
            List {
                Section("广播唤醒测试") {
                    Button(broadcast.armed ? "取消布防" : "布防") {
                        Task { await broadcast.toggle() }
                    }
                    Text(broadcast.status)
                    Button("查看通知延迟") {
                        Task { await broadcast.readNotificationTiming() }
                    }
                    if let timing = broadcast.notificationTiming {
                        Text(timing)
                    }
                }
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
                    .disabled(broadcast.armed)
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
        .onAppear {
            alerts.setBroadcastTestActive(broadcast.armed)
            alerts.setForeground(scenePhase == .active)
        }
        .onChange(of: scenePhase) { alerts.setForeground($0 == .active) }
        .onChange(of: broadcast.armed) { alerts.setBroadcastTestActive($0) }
    }
}

@MainActor
private final class BroadcastWakeTest: NSObject, ObservableObject, CBCentralManagerDelegate, CBPeripheralDelegate {
    private let serviceID = CBUUID(string: "D037E924-FA37-4F34-B1C5-46A2484A4810")
    private let eventID = CBUUID(string: "D037E925-FA37-4F34-B1C5-46A2484A4810")
    private let log = Logger(subsystem: "com.bytereset.unlockpc.watchkitapp", category: "BroadcastWakeTest")
    private let armedPIDKey = "broadcastWakeTest.armedPID"
    private let notificationTimingKey = "broadcastWakeTest.notificationTiming"
    private let pid = ProcessInfo.processInfo.processIdentifier
    private var central: CBCentralManager!
    private var peer: CBPeripheral?
    private var processing = false

    @Published private(set) var armed = false
    @Published private(set) var status = "等待布防"
    @Published private(set) var notificationTiming: String?

    override init() {
        super.init()
        armed = UserDefaults.standard.integer(forKey: armedPIDKey) != 0
        if armed { status = "已布防" }
        log.notice("Process start pid=\(self.pid, privacy: .public), armed=\(self.armed, privacy: .public)")
        central = CBCentralManager(delegate: self, queue: nil, options: [
            CBCentralManagerOptionRestoreIdentifierKey: "com.bytereset.unlockpc.watchkitapp.broadcastWakeTest"
        ])
    }

    func toggle() async {
        if armed {
            finish("已取消布防")
            return
        }
        guard central.state == .poweredOn else {
            status = "蓝牙不可用"
            return
        }
        guard (try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound])) == true else {
            status = "请在系统设置中开启通知"
            return
        }
        UserDefaults.standard.set(Int(pid), forKey: armedPIDKey)
        UserDefaults.standard.removeObject(forKey: notificationTimingKey)
        notificationTiming = nil
        armed = true
        status = "已布防，请按表冠回表盘"
        log.notice("Armed pid=\(self.pid, privacy: .public)")
        startScan()
    }

    func handleBackground() async {
        let deadline = ContinuousClock.now + .seconds(8)
        while (armed || processing) && ContinuousClock.now < deadline {
            do { try await Task.sleep(for: .milliseconds(100)) } catch { return }
        }
    }

    func readNotificationTiming() async {
        guard let sample = UserDefaults.standard.dictionary(forKey: notificationTimingKey),
              let id = sample["id"] as? String,
              let received = sample["received"] as? Date,
              let submitted = sample["submitted"] as? Date else {
            notificationTiming = "还没有广播测试记录"
            return
        }

        let notifications = await UNUserNotificationCenter.current().deliveredNotifications()
        guard let notification = notifications.first(where: { $0.request.identifier == id }) else {
            notificationTiming = "通知中心没有这次测试的记录"
            return
        }
        let receiveToSubmit = Int((submitted.timeIntervalSince(received) * 1000).rounded())
        let submitToDelivery = Int((notification.date.timeIntervalSince(submitted) * 1000).rounded())
        log.notice("Notification timing \(id, privacy: .public) receiveToSubmitMs=\(receiveToSubmit) submitToDeliveryMs=\(submitToDelivery)")
        notificationTiming = "收包 → 提交成功 \(receiveToSubmit) ms\n提交成功 → 系统投递 \(submitToDelivery) ms"
    }

    private func startScan() {
        if armed && peer == nil && central.state == .poweredOn && !central.isScanning {
            central.scanForPeripherals(withServices: [serviceID])
        }
    }

    private func finish(_ message: String) {
        armed = false
        processing = false
        UserDefaults.standard.removeObject(forKey: armedPIDKey)
        if central.isScanning { central.stopScan() }
        if let peer { central.cancelPeripheralConnection(peer) }
        peer = nil
        status = message
    }

    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        startScan()
    }

    func centralManager(_ central: CBCentralManager, willRestoreState state: [String: Any]) {
        if armed {
            log.notice("Core Bluetooth restored pid=\(self.pid, privacy: .public)")
        }
    }

    func centralManager(
        _ central: CBCentralManager, didDiscover peripheral: CBPeripheral,
        advertisementData: [String: Any], rssi: NSNumber
    ) {
        guard armed, peer == nil else { return }
        log.notice("Discovered broadcast pid=\(self.pid, privacy: .public)")
        peer = peripheral
        peripheral.delegate = self
        central.connect(peripheral)
    }

    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        guard peripheral.identifier == peer?.identifier else { return }
        peripheral.discoverServices([serviceID])
    }

    func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
        if peripheral.identifier == peer?.identifier { peer = nil }
    }

    func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        if peripheral.identifier == peer?.identifier { peer = nil }
    }

    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        guard let service = peripheral.services?.first(where: { $0.uuid == serviceID }) else {
            finish("没有找到广播测试服务")
            return
        }
        peripheral.discoverCharacteristics([eventID], for: service)
    }

    func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        guard let value = service.characteristics?.first(where: { $0.uuid == eventID }) else {
            finish("没有找到广播测试特征")
            return
        }
        peripheral.readValue(for: value)
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor value: CBCharacteristic, error: Error?) {
        let received = Date()
        guard armed, !processing, peripheral.identifier == peer?.identifier, value.uuid == eventID,
              let data = value.value, let token = TestAlertPacket.eventID(in: data, now: received) else { return }
        processing = true
        let restarted = Int(pid) != UserDefaults.standard.integer(forKey: armedPIDKey)
        log.notice("Received \(token.uuidString, privacy: .public) pid=\(self.pid, privacy: .public) restarted=\(restarted, privacy: .public)")
        Task {
            let content = UNMutableNotificationContent()
            content.title = "UnlockPC"
            content.body = "收到 Windows 广播测试。"
            content.sound = .default
            do {
                try await UNUserNotificationCenter.current().add(
                    UNNotificationRequest(identifier: token.uuidString, content: content, trigger: nil)
                )
                let submitted = Date()
                if armed, peripheral.identifier == peer?.identifier {
                    var reply = Data(data[2..<18])
                    reply.append(restarted ? 1 : 0)
                    peripheral.writeValue(reply, for: value, type: .withResponse)
                }
                let sample: [String: Any] = ["id": token.uuidString, "received": received, "submitted": submitted]
                UserDefaults.standard.set(sample, forKey: notificationTimingKey)
            } catch {
                finish("系统通知提交失败")
            }
        }
    }

    func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        if characteristic.uuid == eventID {
            finish(error == nil ? "已提交提醒并回传结果" : "已提交提醒，回执发送失败")
        }
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
