import SwiftUI
import UserNotifications
import WatchConnectivity
import WatchKit
@preconcurrency import CoreBluetooth
import CoreLocation
import OSLog

@main
@MainActor
struct UnlockPCWatchApp: App {
    @WKApplicationDelegateAdaptor private var push: WatchPushTest
    @StateObject private var alerts = TestAlerts()
    @StateObject private var broadcast = BroadcastWakeTest()
    @StateObject private var keepAlive = KeepAliveMonitor()

    var body: some Scene {
        WindowGroup {
            WatchHomeView(alerts: alerts, push: push, broadcast: broadcast, keepAlive: keepAlive)
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
    @ObservedObject var keepAlive: KeepAliveMonitor

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
                Section("本地通知对照") {
                    Button("立即发本地通知") {
                        Task { await broadcast.sendLocalNotificationControl() }
                    }
                    .disabled(broadcast.schedulingLocalControl)
                    Button("查看本地通知时间") {
                        Task { await broadcast.readLocalNotificationControl() }
                    }
                    if let status = broadcast.localNotificationControlStatus {
                        Text(status)
                    }
                }
                Section("定位保活与电量") {
                    Text(keepAlive.status)
                    Text("电量记录 \(keepAlive.rowCount) 条")
                    Button("导出电量记录") {
                        push.exportBatteryLog(keepAlive.logURL)
                    }
                    Button("清空电量记录", role: .destructive) {
                        keepAlive.clearLog()
                    }
                }
                Section("直接震动对照") {
                    Button("立即震动") {
                        broadcast.playDirectHaptic(reason: "button")
                    }
                    if let status = broadcast.directHapticStatus {
                        Text(status)
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
                    Text(broadcast.armed ? "广播已布防，普通蓝牙测试已暂停" : alerts.status)
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
            keepAlive.setEnabled(broadcast.armed)
        }
        .onChange(of: scenePhase) {
            alerts.setForeground($0 == .active)
            keepAlive.record($0 == .active ? "foreground" : $0 == .background ? "background" : "inactive")
        }
        .onChange(of: broadcast.armed) {
            alerts.setBroadcastTestActive($0)
            keepAlive.setEnabled($0)
        }
    }
}

@MainActor
private final class BroadcastWakeTest: NSObject, ObservableObject, CBCentralManagerDelegate, CBPeripheralDelegate {
    private let serviceID = CBUUID(string: "D037E924-FA37-4F34-B1C5-46A2484A4810")
    private let eventID = CBUUID(string: "D037E925-FA37-4F34-B1C5-46A2484A4810")
    private let log = Logger(subsystem: "com.bytereset.unlockpc.watchkitapp", category: "BroadcastWakeTest")
    private let armedPIDKey = "broadcastWakeTest.armedPID"
    private let notificationTimingKey = "broadcastWakeTest.notificationTiming"
    private let localControlRequestKey = "broadcastWakeTest.localControlRequest"
    private let pid = ProcessInfo.processInfo.processIdentifier
    private var central: CBCentralManager!
    private var peer: CBPeripheral?
    private var processing = false
    private var exchange = 0
    private var exchangeTimeout: Task<Void, Never>?
    private var lastReplyTime: ContinuousClock.Instant?
    private var lastHandledReplyTime: ContinuousClock.Instant?

    @Published private(set) var armed = false
    @Published private(set) var status = "等待布防"
    @Published private(set) var notificationTiming: String?
    @Published private(set) var schedulingLocalControl = false
    @Published private(set) var localNotificationControlStatus: String?
    @Published private(set) var directHapticStatus: String?

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
            disarm()
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
        armed = true
        status = "已布防，请按表冠回表盘"
        log.notice("Armed pid=\(self.pid, privacy: .public)")
        startScan()
    }

    func handleBackground() async {
        let started = ContinuousClock.now
        let deadline = started + .seconds(8)
        let initialExchange = exchange
        log.notice("Background Bluetooth task started")
        var reason = "timeout or closed exchange"
        defer {
            let elapsed = started.duration(to: .now).components
            let milliseconds = elapsed.seconds * 1000 + elapsed.attoseconds / 1_000_000_000_000_000
            log.notice("Background Bluetooth task returned after \(milliseconds) ms: \(reason, privacy: .public)")
        }
        while armed && !Task.isCancelled {
            let now = ContinuousClock.now
            // Core Bluetooth may finish replying before the SwiftUI background handler starts.
            if let reply = lastReplyTime, reply != lastHandledReplyTime,
               reply.duration(to: now) <= .seconds(4) {
                lastHandledReplyTime = reply
                reason = "reply completed"
                return
            }
            guard exchange == initialExchange, now < deadline else { return }
            do { try await Task.sleep(for: .milliseconds(50)) } catch { return }
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

    func sendLocalNotificationControl() async {
        guard !schedulingLocalControl else { return }
        let requested = Date()
        schedulingLocalControl = true
        defer { schedulingLocalControl = false }
        let center = UNUserNotificationCenter.current()
        guard (try? await center.requestAuthorization(options: [.alert, .sound])) == true else {
            localNotificationControlStatus = "请在系统设置中开启通知"
            return
        }

        let content = UNMutableNotificationContent()
        content.title = "UnlockPC"
        content.body = "本地通知对照。"
        content.sound = .default
        content.interruptionLevel = .timeSensitive
        // A fresh identifier per tap, so the system never silently replaces an already delivered one.
        let id = TestAlerts.localControlPrefix + UUID().uuidString
        do {
            try await center.add(UNNotificationRequest(identifier: id, content: content, trigger: nil))
            UserDefaults.standard.set(["id": id, "requested": requested], forKey: localControlRequestKey)
            log.notice("Local control submitted \(id, privacy: .public)")
            let elapsed = Int((Date().timeIntervalSince(requested) * 1000).rounded())
            localNotificationControlStatus = "系统已接受请求（\(elapsed) ms）"
        } catch {
            localNotificationControlStatus = "本地通知提交失败"
        }
    }

    func readLocalNotificationControl() async {
        guard let sample = UserDefaults.standard.dictionary(forKey: localControlRequestKey),
              let id = sample["id"] as? String,
              let requested = sample["requested"] as? Date else {
            localNotificationControlStatus = "还没有本地通知对照记录"
            return
        }
        let notifications = await UNUserNotificationCenter.current().deliveredNotifications()
        guard let delivered = notifications.first(where: { $0.request.identifier == id }) else {
            localNotificationControlStatus = "通知中心暂无这次投递记录"
            return
        }
        let ms = { (date: Date) in Int((date.timeIntervalSince(requested) * 1000).rounded()) }
        var lines = ["点击 → 系统投递 \(ms(delivered.date)) ms"]
        let present = UserDefaults.standard.dictionary(forKey: TestAlerts.localControlWillPresentKey)
        if present?["id"] as? String == id, let presented = present?["date"] as? Date {
            lines.append("点击 → App 收到 willPresent \(ms(presented)) ms")
        } else {
            lines.append("没有 willPresent 回调（App 不在前台）")
        }
        log.notice("Local control \(id, privacy: .public) \(lines.joined(separator: "; "), privacy: .public)")
        localNotificationControlStatus = lines.joined(separator: "\n")
    }

    func playDirectHaptic(reason: String) {
        let state: String
        switch WKApplication.shared().applicationState {
        case .active: state = "active"
        case .inactive: state = "inactive"
        case .background: state = "background"
        @unknown default: state = "unknown"
        }
        WKInterfaceDevice.current().play(.notification)
        log.notice("Direct haptic played state=\(state, privacy: .public): \(reason, privacy: .public)")
        directHapticStatus = "最近一次直接震动：\(Date().formatted(date: .omitted, time: .standard))（\(state)）"
    }

    private func startScan() {
        if armed && peer == nil && central.state == .poweredOn && !central.isScanning {
            central.scanForPeripherals(withServices: [serviceID])
        }
    }

    private func disarm() {
        armed = false
        lastReplyTime = nil
        UserDefaults.standard.removeObject(forKey: armedPIDKey)
        if central.isScanning { central.stopScan() }
        finishExchange("已取消布防")
    }

    private func finishExchange(_ message: String) {
        exchangeTimeout?.cancel()
        status = armed ? "\(message)。仍在布防，等待下一条。" : message
        processing = false
        if let peer, peer.state != .disconnected {
            central.cancelPeripheralConnection(peer)
        } else {
            resumeScan()
        }
    }

    private func resumeScan() {
        exchangeTimeout?.cancel()
        exchangeTimeout = nil
        peer = nil
        processing = false
        exchange += 1
        startScan()
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
        central.stopScan()
        peripheral.delegate = self
        central.connect(peripheral)
        exchangeTimeout = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(5)) } catch { return }
            self?.finishExchange("本次广播连接超时")
        }
    }

    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        guard armed, peripheral.identifier == peer?.identifier else { return }
        peripheral.discoverServices([serviceID])
    }

    func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
        if peripheral.identifier == peer?.identifier { resumeScan() }
    }

    func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        if peripheral.identifier == peer?.identifier { resumeScan() }
    }

    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        guard armed, peripheral.identifier == peer?.identifier else { return }
        guard error == nil, let service = peripheral.services?.first(where: { $0.uuid == serviceID }) else {
            finishExchange("没有找到广播测试服务")
            return
        }
        peripheral.discoverCharacteristics([eventID], for: service)
    }

    func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        guard armed, peripheral.identifier == peer?.identifier else { return }
        guard error == nil, let value = service.characteristics?.first(where: { $0.uuid == eventID }) else {
            finishExchange("没有找到广播测试特征")
            return
        }
        peripheral.readValue(for: value)
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor value: CBCharacteristic, error: Error?) {
        let received = Date()
        guard armed, !processing, peripheral.identifier == peer?.identifier, value.uuid == eventID else { return }
        guard error == nil, let data = value.value, let token = TestAlertPacket.eventID(in: data, now: received) else {
            finishExchange("没有读到有效的广播测试数据")
            return
        }
        processing = true
        let currentExchange = exchange
        let restarted = Int(pid) != UserDefaults.standard.integer(forKey: armedPIDKey)
        var reply = Data(data[2..<18])
        reply.append(restarted ? 1 : 0)
        if UserDefaults.standard.dictionary(forKey: notificationTimingKey)?["id"] as? String == token.uuidString {
            peripheral.writeValue(reply, for: value, type: .withResponse)
            return
        }
        log.notice("Received \(token.uuidString, privacy: .public) pid=\(self.pid, privacy: .public) restarted=\(restarted, privacy: .public)")
        // Direct haptic bypasses the notification presentation path; it may be ignored in the background.
        playDirectHaptic(reason: "broadcast \(token.uuidString)")
        Task {
            let content = UNMutableNotificationContent()
            content.title = "UnlockPC"
            content.body = "收到 Windows 广播测试。"
            content.sound = .default
            content.interruptionLevel = .timeSensitive
            do {
                try await UNUserNotificationCenter.current().add(
                    UNNotificationRequest(identifier: token.uuidString, content: content, trigger: nil)
                )
                let submitted = Date()
                guard armed, currentExchange == exchange, processing else { return }
                peripheral.writeValue(reply, for: value, type: .withResponse)
                let sample: [String: Any] = ["id": token.uuidString, "received": received, "submitted": submitted]
                UserDefaults.standard.set(sample, forKey: notificationTimingKey)
                notificationTiming = nil
            } catch {
                if armed, currentExchange == exchange, processing { finishExchange("系统通知提交失败") }
            }
        }
    }

    func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        if armed, processing, peripheral.identifier == peer?.identifier, characteristic.uuid == eventID {
            lastReplyTime = .now
            finishExchange(error == nil ? "已提交提醒并回传结果" : "已提交提醒，回执发送失败")
        }
    }
}

/// Keeps the app running in the background with low-accuracy location while the broadcast test is armed,
/// and logs battery level once a minute. Gaps between "tick" rows show when the app was suspended.
@MainActor
private final class KeepAliveMonitor: NSObject, ObservableObject, CLLocationManagerDelegate {
    let logURL = URL.documentsDirectory.appending(path: "battery-log.csv")
    private let header = "time,uptimeSec,batteryPct,batteryState,keepAlive,locationUpdates,appState,gapSec,event\n"
    private let log = Logger(subsystem: "com.bytereset.unlockpc.watchkitapp", category: "KeepAlive")
    private let manager = CLLocationManager()
    private var backgroundActivity: AnyObject?
    private var wanted = false
    private var running = false
    private var locationUpdates = 0
    private var lastTick: Date?

    @Published private(set) var status = "未布防，定位保活关闭"
    @Published private(set) var rowCount = 0

    override init() {
        super.init()
        manager.delegate = self
        // Only the background runtime matters here, not the position.
        manager.desiredAccuracy = kCLLocationAccuracyThreeKilometers
        manager.distanceFilter = kCLDistanceFilterNone
        WKInterfaceDevice.current().isBatteryMonitoringEnabled = true
        rowCount = max(0, ((try? String(contentsOf: logURL, encoding: .utf8))?
            .split(separator: "\n").count ?? 1) - 1)
        record("launch")
        Task { [weak self] in
            while !Task.isCancelled {
                do { try await Task.sleep(for: .seconds(60)) } catch { return }
                self?.record("tick")
            }
        }
    }

    func setEnabled(_ enabled: Bool) {
        wanted = enabled
        enabled ? start() : stop()
    }

    private func start() {
        guard wanted, !running else { return }
        switch manager.authorizationStatus {
        case .notDetermined:
            status = "等待定位授权"
            manager.requestWhenInUseAuthorization()
            return
        case .denied, .restricted:
            status = "定位权限未开启，无法保活"
            record("locationDenied")
            return
        default:
            break
        }
        manager.allowsBackgroundLocationUpdates = true
        manager.startUpdatingLocation()
        if #available(watchOS 10.0, *) {
            backgroundActivity = CLBackgroundActivitySession()
        }
        running = true
        status = "定位保活已开启"
        record("keepAliveOn")
        log.notice("Keep-alive started")
    }

    private func stop() {
        guard running else { return }
        manager.stopUpdatingLocation()
        manager.allowsBackgroundLocationUpdates = false
        if #available(watchOS 10.0, *) {
            (backgroundActivity as? CLBackgroundActivitySession)?.invalidate()
        }
        backgroundActivity = nil
        running = false
        status = "未布防，定位保活关闭"
        record("keepAliveOff")
        log.notice("Keep-alive stopped")
    }

    func record(_ event: String) {
        let now = Date()
        let device = WKInterfaceDevice.current()
        let battery = device.batteryLevel < 0 ? "" : String(format: "%.0f", device.batteryLevel * 100)
        let charge: String
        switch device.batteryState {
        case .charging: charge = "charging"
        case .full: charge = "full"
        case .unplugged: charge = "unplugged"
        default: charge = "unknown"
        }
        let app: String
        switch WKApplication.shared().applicationState {
        case .active: app = "active"
        case .inactive: app = "inactive"
        case .background: app = "background"
        @unknown default: app = "unknown"
        }
        let gap = event == "tick" ? lastTick.map { String(format: "%.0f", now.timeIntervalSince($0)) } ?? "" : ""
        if event == "tick" || event == "launch" { lastTick = now }
        let row = [
            now.ISO8601Format(), String(format: "%.0f", ProcessInfo.processInfo.systemUptime), battery, charge,
            running ? "1" : "0", String(locationUpdates), app, gap, event,
        ].joined(separator: ",") + "\n"
        locationUpdates = 0
        append(row)
    }

    func clearLog() {
        try? FileManager.default.removeItem(at: logURL)
        rowCount = 0
        lastTick = nil
        record("cleared")
    }

    private func append(_ row: String) {
        do {
            if !FileManager.default.fileExists(atPath: logURL.path) {
                try Data(header.utf8).write(to: logURL)
            }
            let handle = try FileHandle(forWritingTo: logURL)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: Data(row.utf8))
            rowCount += 1
        } catch {
            log.error("Battery log write failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    nonisolated func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
        Task { @MainActor in self.start() }
    }

    nonisolated func locationManager(_ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        let count = locations.count
        Task { @MainActor in self.locationUpdates += count }
    }

    nonisolated func locationManager(_ manager: CLLocationManager, didFailWithError error: Error) {
        let message = error.localizedDescription
        Task { @MainActor in self.log.error("Location error: \(message, privacy: .public)") }
    }
}

@MainActor
private final class WatchPushTest: NSObject, ObservableObject, WKApplicationDelegate, WCSessionDelegate {
    @Published private(set) var status = "点按注册，准备 Windows 解锁实测"
    private var pendingToken: String?
    private var pendingBatteryLog: URL?

    func exportBatteryLog(_ url: URL) {
        guard WCSession.isSupported(), FileManager.default.fileExists(atPath: url.path) else {
            status = "还没有电量记录"
            return
        }
        // Send a snapshot so later appends don't race the transfer.
        let snapshot = FileManager.default.temporaryDirectory.appending(path: "battery-log-\(UUID().uuidString).csv")
        do {
            try FileManager.default.copyItem(at: url, to: snapshot)
        } catch {
            status = "电量记录复制失败"
            return
        }
        let session = WCSession.default
        guard session.activationState == .activated else {
            pendingBatteryLog = snapshot
            session.delegate = self
            session.activate()
            status = "正在连接 iPhone，随后导出电量记录"
            return
        }
        session.transferFile(snapshot, metadata: ["unlockpcWatchBatteryLog": true])
        status = "已开始把电量记录传到 iPhone"
    }

    private func flushBatteryLog() {
        guard let pending = pendingBatteryLog, WCSession.default.activationState == .activated else { return }
        pendingBatteryLog = nil
        WCSession.default.transferFile(pending, metadata: ["unlockpcWatchBatteryLog": true])
        status = "已开始把电量记录传到 iPhone"
    }

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
                flushBatteryLog()
            }
        }
    }

    nonisolated func session(_ session: WCSession, didFinish fileTransfer: WCSessionFileTransfer, error: (any Error)?) {
        guard fileTransfer.file.metadata?["unlockpcWatchBatteryLog"] != nil else { return }
        let failure = error?.localizedDescription
        try? FileManager.default.removeItem(at: fileTransfer.file.fileURL)
        Task { @MainActor in
            status = failure.map { "电量记录传输失败：\($0)" } ?? "电量记录已送达 iPhone"
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
