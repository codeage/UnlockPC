# UnlockPC Windows 与 Apple Watch 对接

Windows 是 BLE GATT 外设，Apple Watch 在前台选择电脑并建立订阅。手表直接接收 Windows 的 Notify，校验后提交本地通知。iPhone 不参与这条链路。

## 当前可用的测试协议

| 字段 | 值 |
| --- | --- |
| GATT 服务 UUID | `8A29C90B-A244-4BC2-9DB9-B7CC794471E3` |
| Notify 特征 UUID | `C5AEE773-8378-4A75-A218-21505DC61F73` |
| 字节 0 | 协议版本 `0x01` |
| 字节 1 | 测试事件 `0x01` |
| 字节 2–17 | 事件 UUID v4，网络字节序 |
| 字节 18–21 | UTC Unix 秒，无符号 32 位大端整数 |
| 字节 22–53 | 前 22 字节的 HMAC-SHA256。密钥为 Windows 测试发送器显示的 20 位数字的 ASCII 字节 |

Windows 参考实现见 `tools/WatchAlertTest/Program.cs`。手表解析见 `Apple/Watch/TestAlertPacket.swift`。测试发送器只向操作员选定且在线订阅的客户端发送。手表接受最多 30 秒前、至多 5 秒后的测试事件，按事件 UUID 去重。

## 真实解锁提醒的接入条件

1. 在 Windows 用户会话后台进程发出提醒。按同一尝试 ID 和目标会话关联凭据提供程序的可信认证结果与 `WTS_SESSION_UNLOCK`，并实测回调和会话事件的先后顺序。能够归因给 UnlockPC 时才发出一次提醒。
2. 正式事件保留类型 `0x02 unlockSucceeded`，Windows 和 Watch 两端一起启用。Watch 当前解析器只接受 `0x01` 测试事件。
3. 正式发包前验证已授权配对和事件来源。测试码及控制台手动选择订阅者是初次真表验证的配置。业务事件使用明确授权的设备身份和经过验证的来源保护。
4. 关闭提醒、断线、通知权限关闭和发送失败均不影响 Windows 解锁。每次确认的解锁使用独立事件 UUID，断线不补发。日志保留事件 ID、发送结果和时间，不记录密码、PIN 或测试码。
