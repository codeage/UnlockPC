# UnlockPC for iPhone and Apple Watch

用支持 iOS 16 和 watchOS 9.2 或更新版本 SDK 的 Xcode 打开 `UnlockPC.xcodeproj`。真机调试还需要 Xcode 支持设备实际运行的系统版本。

| Scheme | Bundle ID | 用途 |
| --- | --- | --- |
| `UnlockPC` | `com.bytereset.unlockpc` | 运行配套 iPhone App，归档时嵌入 Watch App |
| `UnlockPC Watch` | `com.bytereset.unlockpc.watchkitapp` | 直接运行 Watch App，BLE 提醒链路独立于 iPhone App |

两个 App 的显示名都是 **UnlockPC**。签名配置使用现有 ByteReset 团队和自动签名。分发配置拟用 SKU `UnlockPC-Watch`，TestFlight 内测组拟用 `UnlockPC Watch Internal`。

正式接入 Windows 解锁流程见 [Windows 对接说明](Windows-Integration.md)。暂用 App 图标采用 Microsoft Fluent UI System Icons 的 Lock Open 图形，许可证见 [FluentIcon-LICENSE.txt](FluentIcon-LICENSE.txt)。

## 离线 BLE 测试提醒

用支持 BLE 外设模式的 Windows 11 电脑和兼容的实体 Apple Watch。第一轮后台测试优先使用 Apple Watch Series 6 或更新机型，并记录型号和系统版本。保持电脑和手表的系统时间准确。

在 Windows 开发版中，连续点击「关于」页的版本号 7 次打开「调试」，然后在「Apple Watch 测试提醒」里开启发送。手表搜索并选择电脑，显示已订阅后，在 Windows 点击「发送测试提醒」或「15 秒后发送」。

也可通过独立命令行工具测试。

1. 在 Windows 本地控制台运行下方命令，等待 GATT 开始广播。
2. 选 `UnlockPC Watch` scheme 安装到手表，打开 App 并允许通知，然后搜索并选择测试电脑。
3. 手表显示已订阅后，在 Windows 控制台按回车。只有一个手表订阅时才发送。
4. 核对 Windows 的事件 ID 和 GATT 发送结果、Watch App 的最近收包时间、手表本地通知及实际震感。用同一手表脱离调试器并回到表盘，然后复测后台和离线发送。验证 Windows 锁屏时，输入 `D` 回车，在 15 秒内按 `Win+L`。
5. 输入 `Q` 回车停止广播。需要更换电脑时，在手表选择「忘记测试电脑」。

```powershell
dotnet run --project tools/WatchAlertTest/WatchAlertTest.csproj -c Release
```

命令从仓库根目录运行。每次发送使用当前订阅，断线期间不排队。测试包是单次 22 字节的 GATT Notify。手表只接收选定设备的 v2 测试事件，并检查事件时间和 UUID。测试链路依靠显式选择电脑，无应用层来源认证。真实解锁事件需先实现设备授权与来源验证。
