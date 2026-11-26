import Combine
@preconcurrency import CoreBluetooth
import Foundation
import OSLog
import UserNotifications

@MainActor
final class TestAlerts: NSObject, ObservableObject {
    struct Candidate: Identifiable {
        let id: UUID
        let label: String
    }

    private struct DiscoveryProbe {
        let peripheral: CBPeripheral
        let label: String
        let timeout: Task<Void, Never>
    }

    @Published private(set) var status = "正在准备蓝牙"
    @Published private(set) var candidates: [Candidate] = []
    @Published private(set) var isScanning = false
    @Published private(set) var hasSelection = false
    @Published private(set) var lastReceived: Date?

    private let service = CBUUID(string: TestAlertPacket.serviceID)
    private let compatibilityService = CBUUID(string: "180D")
    private let characteristic = CBUUID(string: TestAlertPacket.characteristicID)
    private let preferences = UserDefaults.standard
    private let notificationCenter = UNUserNotificationCenter.current()
    private let log = Logger(subsystem: "com.bytereset.unlockpc.watchkitapp", category: "TestAlerts")
    private let selectionKey = "testAlerts.selectedPeripheral"
    private let recentEventsKey = "testAlerts.recentEvents"
    private var central: CBCentralManager!
    private var selection: UUID?
    private var peer: CBPeripheral?
    private var discovered: [UUID: CBPeripheral] = [:]
    private var probes: [UUID: DiscoveryProbe] = [:]
    private var probed: Set<UUID> = []
    private var isForeground = false
    private var scanTimeout: Task<Void, Never>?
    private var autoConnectTask: Task<Void, Never>?
    private var notificationTask: Task<Void, Never>?
    private var lastReceiptTime: ContinuousClock.Instant?
    private var lastBackgroundReceiptTime: ContinuousClock.Instant?

    override init() {
        super.init()
        selection = preferences.string(forKey: selectionKey).flatMap { UUID(uuidString: $0) }
        hasSelection = selection != nil
        notificationCenter.delegate = self
        // Keep this identifier stable so Core Bluetooth can restore the selected connection.
        central = CBCentralManager(delegate: self, queue: nil, options: [
            CBCentralManagerOptionRestoreIdentifierKey: "com.bytereset.unlockpc.watchkitapp.central"
        ])
    }

    func toggleScan() {
        if isScanning {
            stopScan()
            status = "搜索已停止"
            return
        }
        guard central.state == .poweredOn else {
            status = "蓝牙当前不可用"
            return
        }
        candidates.removeAll()
        discovered.removeAll()
        probed.removeAll()
        isScanning = true
        status = "正在搜索测试电脑"
        central.scanForPeripherals(withServices: [service, compatibilityService])
        scanTimeout = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(15)) } catch { return }
            guard let self else { return }
            self.stopScan()
            self.status = self.candidates.isEmpty ? "未找到测试电脑" : "请选择测试电脑"
        }
    }

    func setForeground(_ foreground: Bool) {
        guard foreground != isForeground else { return }
        isForeground = foreground
        if foreground {
            if !hasSelection && central.state == .poweredOn && !isScanning {
                toggleScan()
            }
        } else if isScanning {
            stopScan()
            status = candidates.isEmpty ? "搜索已停止" : "请选择测试电脑"
        }
    }

    func select(_ id: UUID) {
        guard let peripheral = discovered[id] else { return }
        stopScan(keeping: id)
        candidates.removeAll()
        discovered.removeAll()
        if let previous = peer, previous.identifier != id {
            central.cancelPeripheralConnection(previous)
        }
        selection = id
        hasSelection = true
        preferences.set(id.uuidString, forKey: selectionKey)
        attach(peripheral)
    }

    func forget() {
        stopScan()
        if let peer { central.cancelPeripheralConnection(peer) }
        peer = nil
        selection = nil
        hasSelection = false
        lastReceived = nil
        lastReceiptTime = nil
        notificationTask?.cancel()
        candidates.removeAll()
        discovered.removeAll()
        preferences.removeObject(forKey: selectionKey)
        preferences.removeObject(forKey: recentEventsKey)
        status = "已忘记测试电脑"
    }

    func handleBackgroundAlert() async {
        let deadline = ContinuousClock.now + .seconds(3)
        // Core Bluetooth can deliver the value just before or just after the system task starts.
        while !Task.isCancelled && ContinuousClock.now < deadline {
            if let receipt = lastReceiptTime, receipt != lastBackgroundReceiptTime,
               receipt.duration(to: .now) <= .seconds(4) {
                lastBackgroundReceiptTime = receipt
                await notificationTask?.value
                return
            }
            do { try await Task.sleep(for: .milliseconds(150)) } catch { return }
        }
    }

    private func stopScan(keeping id: UUID? = nil) {
        scanTimeout?.cancel()
        scanTimeout = nil
        autoConnectTask?.cancel()
        autoConnectTask = nil
        if central.isScanning { central.stopScan() }
        isScanning = false
        for peripheral in discovered.values where peripheral.identifier != id && peripheral.identifier != selection {
            central.cancelPeripheralConnection(peripheral)
        }
        for probe in probes.values {
            probe.timeout.cancel()
            if probe.peripheral.identifier != id && probe.peripheral.identifier != selection {
                central.cancelPeripheralConnection(probe.peripheral)
            }
        }
        probes.removeAll()
    }

    private func reconnectIfNeeded() {
        guard central.state == .poweredOn, let selection else { return }
        if let peripheral = peer ?? central.retrievePeripherals(withIdentifiers: [selection]).first {
            attach(peripheral)
        } else {
            status = "请重新搜索已选电脑"
        }
    }

    private func attach(_ peripheral: CBPeripheral) {
        peer = peripheral
        peripheral.delegate = self
        switch peripheral.state {
        case .connected:
            peripheral.discoverServices([service, compatibilityService])
        case .disconnected:
            status = "正在连接测试电脑"
            central.connect(peripheral)
        default:
            break
        }
    }

    private func finishProbe(_ peripheral: CBPeripheral, valid: Bool) {
        let id = peripheral.identifier
        guard let probe = probes.removeValue(forKey: id) else { return }
        probe.timeout.cancel()
        if valid && isScanning {
            addCandidate(peripheral, label: probe.label)
        } else if id != selection {
            central.cancelPeripheralConnection(peripheral)
        }
    }

    private func addCandidate(_ peripheral: CBPeripheral, label: String) {
        let id = peripheral.identifier
        guard isScanning, discovered[id] == nil else { return }
        discovered[id] = peripheral
        candidates.append(Candidate(id: id, label: label))
        autoConnectTask?.cancel()
        guard candidates.count == 1 else {
            status = "找到多台测试电脑，请选择"
            return
        }
        autoConnectTask = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(2)) } catch { return }
            guard let self, self.isForeground, self.isScanning, self.candidates.count == 1 else { return }
            self.select(id)
        }
    }

    private func receive(_ packet: Data) {
        let now = Date()
        guard let id = TestAlertPacket.eventID(in: packet, now: now) else {
            report("已丢弃无效或过期的测试事件")
            return
        }

        let idString = id.uuidString.lowercased()
        var recent = preferences.dictionary(forKey: recentEventsKey)?
            .compactMapValues { $0 as? Double } ?? [:]
        recent = recent.filter { now.timeIntervalSince1970 - $0.value <= 40 }
        guard recent[idString] == nil else { return }
        recent[idString] = now.timeIntervalSince1970
        preferences.set(recent, forKey: recentEventsKey)
        lastReceived = now
        lastReceiptTime = .now
        status = "已收到测试事件"
        log.notice("Received test event \(idString, privacy: .public)")

        notificationTask = Task { [weak self] in
            await self?.postNotification(id: idString)
        }
    }

    private func postNotification(id: String) async {
        let settings = await notificationCenter.notificationSettings()
        guard !Task.isCancelled else { return }
        guard settings.authorizationStatus == .authorized ||
              settings.authorizationStatus == .provisional else {
            report("已收到事件，通知权限未开启")
            return
        }
        let content = UNMutableNotificationContent()
        content.title = "UnlockPC"
        content.body = "已收到电脑发送的蓝牙测试提醒。"
        content.sound = .default
        do {
            try await notificationCenter.add(
                UNNotificationRequest(identifier: "unlockpc.\(id)", content: content, trigger: nil)
            )
            log.notice("Scheduled local test notification \(id, privacy: .public)")
        } catch {
            report("本地通知请求失败", error: error)
        }
    }

    private func report(_ message: String, error: Error? = nil) {
        status = message
        if let error = error as NSError? {
            log.error("\(message, privacy: .public) [\(error.domain, privacy: .public):\(error.code)]")
        } else {
            log.notice("\(message, privacy: .public)")
        }
    }
}

extension TestAlerts: CBCentralManagerDelegate, CBPeripheralDelegate {
    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        guard central.state == .poweredOn else {
            stopScan()
            status = central.state == .unknown || central.state == .resetting
                ? "正在准备蓝牙" : "蓝牙不可用，请检查设置"
            return
        }
        if hasSelection {
            reconnectIfNeeded()
        } else if isForeground && !isScanning {
            toggleScan()
        } else {
            status = "等待搜索测试电脑"
        }
    }

    func centralManager(_ central: CBCentralManager, willRestoreState state: [String: Any]) {
        guard let selection,
              let peripherals = state[CBCentralManagerRestoredStatePeripheralsKey] as? [CBPeripheral],
              let restored = peripherals.first(where: { $0.identifier == selection }) else { return }
        peer = restored
        restored.delegate = self
    }

    func centralManager(
        _ central: CBCentralManager, didDiscover peripheral: CBPeripheral,
        advertisementData: [String: Any], rssi RSSI: NSNumber
    ) {
        guard isScanning, discovered[peripheral.identifier] == nil else { return }
        let name = (advertisementData[CBAdvertisementDataLocalNameKey] as? String)
            ?? peripheral.name ?? "测试电脑"
        let label = "\(name) · \(peripheral.identifier.uuidString.suffix(4))"
        let advertised = (advertisementData[CBAdvertisementDataServiceUUIDsKey] as? [CBUUID] ?? [])
            + (advertisementData[CBAdvertisementDataOverflowServiceUUIDsKey] as? [CBUUID] ?? [])
        if advertised.contains(service) {
            addCandidate(peripheral, label: label)
            return
        }
        let id = peripheral.identifier
        guard advertisementData[CBAdvertisementDataIsConnectable] as? Bool != false,
              probes.count < 4, probed.insert(id).inserted else { return }
        let timeout = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(4)) } catch { return }
            self?.finishProbe(peripheral, valid: false)
        }
        probes[id] = DiscoveryProbe(peripheral: peripheral, label: label, timeout: timeout)
        peripheral.delegate = self
        if peripheral.state == .connected {
            peripheral.discoverServices([service, compatibilityService])
        } else if peripheral.state == .disconnected {
            central.connect(peripheral)
        }
    }

    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        guard peripheral.identifier == selection || probes[peripheral.identifier] != nil else { return }
        peripheral.discoverServices([service, compatibilityService])
    }

    func centralManager(
        _ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?
    ) {
        finishProbe(peripheral, valid: false)
        guard peripheral.identifier == selection else { return }
        report("连接失败，请重新搜索电脑", error: error)
    }

    func centralManager(
        _ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?
    ) {
        finishProbe(peripheral, valid: false)
        guard peripheral.identifier == selection, central.state == .poweredOn else { return }
        report("连接中断，等待重新连接", error: error)
        central.connect(peripheral)
    }

    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        let id = peripheral.identifier
        guard id == selection || probes[id] != nil else { return }
        guard error == nil, let match = peripheral.services?.first(where: { $0.uuid == service })
                  ?? peripheral.services?.first(where: { $0.uuid == compatibilityService }) else {
            finishProbe(peripheral, valid: false)
            if id == selection { report("找不到测试提醒服务", error: error) }
            return
        }
        peripheral.discoverCharacteristics([characteristic], for: match)
    }

    func peripheral(
        _ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?
    ) {
        let id = peripheral.identifier
        guard id == selection || probes[id] != nil else { return }
        guard error == nil,
              let value = service.characteristics?.first(where: { $0.uuid == characteristic }),
              value.properties.contains(.notify) else {
            finishProbe(peripheral, valid: false)
            if id == selection { report("找不到测试提醒特征", error: error) }
            return
        }
        finishProbe(peripheral, valid: true)
        guard id == selection else { return }
        if value.isNotifying {
            status = "已订阅，等待电脑发送测试提醒"
        } else {
            peripheral.setNotifyValue(true, for: value)
        }
    }

    func peripheral(
        _ peripheral: CBPeripheral, didUpdateNotificationStateFor value: CBCharacteristic, error: Error?
    ) {
        guard peripheral.identifier == selection, value.uuid == characteristic else { return }
        if error == nil, value.isNotifying {
            status = "已订阅，等待电脑发送测试提醒"
        } else {
            report("无法订阅测试提醒", error: error)
        }
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor value: CBCharacteristic, error: Error?) {
        guard peripheral.identifier == selection, value.uuid == characteristic, value.isNotifying else { return }
        guard error == nil, let packet = value.value else {
            report("蓝牙提醒收包失败", error: error)
            return
        }
        receive(packet)
    }
}

extension TestAlerts: UNUserNotificationCenterDelegate {
    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter, willPresent notification: UNNotification
    ) async -> UNNotificationPresentationOptions {
        [.banner, .sound]
    }
}
