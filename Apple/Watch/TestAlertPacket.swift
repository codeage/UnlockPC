import Foundation

enum TestAlertPacket {
    static let serviceID = "8A29C90B-A244-4BC2-9DB9-B7CC794471E3"
    static let characteristicID = "C5AEE773-8378-4A75-A218-21505DC61F73"
    static let freshnessWindow: TimeInterval = 30

    static func eventID(in packet: Data, now: Date) -> UUID? {
        // Version, test event type, UUID in network order, Unix seconds in big-endian order.
        guard packet.count == 22 else { return nil }
        let bytes = Array(packet)
        guard bytes[0] == 2, bytes[1] == 1 else { return nil }
        let timestamp = bytes[18..<22].reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
        let age = now.timeIntervalSince1970 - Double(timestamp)
        guard age >= -5, age <= freshnessWindow else { return nil }
        return UUID(uuid: (
            bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7], bytes[8], bytes[9],
            bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15], bytes[16], bytes[17]
        ))
    }
}
