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

1. 在 Windows 本地控制台运行下方命令。等待 GATT 开始广播，并保留控制台上的一次性运行测试码。
2. 选 `UnlockPC Watch` scheme 安装到手表，打开 App，允许通知。输入 Windows 显示的 20 位测试码，保存后搜索并选择测试电脑。Windows 发送器每次启动都会生成新码。
3. 手表显示已订阅后，确认测试电脑上只有本次测试的手表正在订阅。在 Windows 控制台输入 `A` 回车授权该订阅者，再单独按回车发送一条测试提醒。
4. 核对 Windows 的事件 ID 和 GATT 发送结果、Watch App 的最近收包时间、手表本地通知及实际震感。用同一手表脱离调试器并回到表盘，然后复测后台和离线发送。验证 Windows 锁屏时，输入 `D` 回车，在 15 秒内按 `Win+L`。发送器到时只向仍在线的已授权订阅者发送一次。
5. 输入 `Q` 回车停止广播。需要更换电脑时，在手表选择「忘记测试电脑」。

```powershell
dotnet run --project tools/WatchAlertTest/WatchAlertTest.csproj -c Release
```

命令从仓库根目录运行。每次发送使用当前订阅，断线期间不排队。测试包是单次 GATT Notify，要求协商后的通知上限至少为 54 字节。手表校验 HMAC、事件时间和 UUID，重复事件或超过 30 秒的旧事件不会弹出通知。原型发送器只产生测试事件，真实 Windows 解锁事件的归因与转发留待链路验证后接入。
