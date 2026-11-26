import SwiftUI

@main
struct UnlockPCApp: App {
    var body: some Scene {
        WindowGroup {
            NavigationStack {
                List {
                    Section("Apple Watch") {
                        Text("在 Apple Watch 上打开 UnlockPC，选择测试电脑并设置提醒。")
                        Text("手表通过蓝牙直接接收提醒。")
                            .foregroundStyle(.secondary)
                    }
                }
                .navigationTitle("UnlockPC")
            }
        }
    }
}
