# UnlockPC

Windows 11 x64 的 WinUI 3 原型，可观察 Apple Watch 的蓝牙广播。支持靠近亮屏后使用 Windows Hello，也提供使用 Windows 凭据提供程序的实验性自动解锁。

iPhone 与 Apple Watch 的 GitHub 构建、Ad Hoc 直装和 TestFlight 内测流程见 [Apple 编译安装](Apple/Build-and-Install.md)。

## 构建

在安装了 .NET 10 SDK、PowerShell 7 和 Visual Studio C++ 桌面开发工具（含 Windows SDK）的 Windows 上运行：

```powershell
dotnet build UnlockPC.slnx -c Release -p:Platform=x64
```

每个 Windows 用户会话只运行一个 UnlockPC。再次启动会打开已有窗口，`--pair` 会把配对请求交给已有实例。Visual Studio 和命令行构建会先发送退出事件，等旧进程结束后再构建。

构建前会停止 UnlockPC IRK 服务，构建成功后会安装或更新服务并启动一次。首次安装、变更授权用户或更新服务程序时需要管理员确认。服务设为手动启动，构建时指定的 Windows 用户可启动和停止该服务。应用在监测已配对手表时按需启动服务，结束监测时服务退出。只构建产物的环境可指定 `-p:DeployIrkService=false`。

运行 `UnlockPC/bin/x64/Release/net10.0-windows10.0.22000.0/win-x64/UnlockPC.exe`。Release 构建会将 Windows 登录组件一起放进该目录，分发时请复制整个目录。窗口布局来自 TrayPalette/FolderTool `b9f7705`，使用随项目附带的 WinUIKit `184dc1f`。

## 域账户单元测试

构建 Release x64 后，运行 `pwsh -File scripts/Test-DomainUnlock.ps1` 可执行域账户解锁的代码级单元测试。测试使用模拟账户和会话数据，覆盖凭据身份、登录组件注册要求以及原生组件的会话筛选和凭据序列化。无需加入域、安装登录组件或锁屏。

## 界面自检

锁屏与解锁自检须在本机控制台运行。先从系统托盘退出普通 UnlockPC，再运行下列命令并按提示输入本机账户密码。测试会真实锁屏 5 次。每轮锁屏后用两个相隔约 3 秒的弱信号模拟离开，再用一条强信号模拟返回。自检核对登录组件是否领取凭据，并在首个失败轮次停止。

```powershell
pwsh -File scripts/Test-LockCycle.ps1
```

以下自检用模拟时间和 RSSI 覆盖靠近、离开、锁屏和系统恢复状态。添加 `-IncludeAdvancedOptions` 同时检查高级选项的两个开关。自检无需让电脑睡眠或锁屏。

```powershell
pwsh -File scripts/Test-Presence.ps1
```

## 手表管理与绑定

在左侧第二页「手表管理」点击「绑定手表」，也可用 --pair 打开此页。UnlockPC 同时绑定一块手表。Windows 中已经配对的 Apple Watch 可以直接绑定；有多块手表时，先选择要绑定的一块。已配对但改名的手表可以用「使用已配对设备」手动选择。

新配对需要电脑支持 BLE 外设模式。UnlockPC 自动开启最多 120 秒的配对广播。在手表上打开「设置 → 蓝牙」，选择电脑并按手表和 Windows 的提示确认。配对后，UnlockPC 会自动启动 IRK 服务读取这块手表的配对密钥。

服务验证手表广播后，UnlockPC 保存设备绑定并继续检测。服务从 Windows 蓝牙配对记录读取 IRK，仅向应用返回地址是否匹配。UnlockPC 的绑定文件保存设备标识和可选的账户凭据。向导可选择是否开启密码式自动解锁。跳过后，可在「手表管理」打开开关并验证当前 Windows 登录密码。「移除手表」会解除 Windows 配对并清除本地绑定与解锁凭据。

配对期间，UnlockPC 订阅 Windows 系统日志中的设备关联结果。系统报告配对失败时，向导显示失败阶段和错误码。JSONL 中的 配对成功或失败事件均含 RecordId，可据此定位原始系统事件。

## 后台手表检测

Release 构建和安装部署 UnlockPCMonitor。该服务随 Windows 启动，直接在 Session 0 扫描蓝牙广播，并使用系统保存的手表配对记录完成 IRK 匹配。授权账户登录后，服务启动独立的用户会话进程。该进程读取服务的检测结果，执行与用户会话相关的锁定和解锁。手表管理界面读取检测状态并保存设置。退出桌面界面后，后台检测继续运行。要停用自动动作，请关闭手表管理页的对应开关。

Windows 睡眠或休眠恢复后，检测服务重置扫描状态和广播队列，再启动一轮扫描。用户会话进程同时取消尚未完成的验证。首次开机登录仍使用 Windows 登录方式。Windows 服务控制管理器会在检测服务故障后重新启动它。管理员仍可手动停止服务。

## 蓝牙诊断日志

Windows 服务另写一份独立日志，路径为 %ProgramFiles%\UnlockPC IRK Service\Logs\watch-monitor.jsonl。服务开机、扫描启动、首次 IRK 匹配、会话进程启停和睡眠恢复都会记入该文件。扫描每 15 秒生成一条统计，含本机控制台是否有人登录及本轮 IRK 命中数。注销复测时留在登录界面至少 30 秒，再登录查看 consoleUserPresent 为 false 的记录。日志按 4 MiB 轮转并保留上一份。

日志保存在 `%LOCALAPPDATA%\UnlockPC\Logs`。每次启动生成一个 JSONL 文件，暂不限制大小。扫描时，只有通过 Windows 配对地址或 IRK 识别为当前选中手表的广播才会逐条写入 `scan_watch_advertisement`。每条记录包含接收时间、RSSI、广播类型、内容摘要、识别方式，以及是否进入自动锁屏和解锁使用的采样队列。配对、扫描状态和锁屏解锁事件继续写入同一文件。地址用同一进程内的编号表示。`scan_windows_address_changed` 表示 Windows 提供的地址变了，无法独立确认空中的私有地址轮换。

左侧「手表当前状态」解析已匹配广播的 Nearby Info 和 Magic Switch 字段。锁定状态根据 Nearby Info 数据标志按位推断。仅 `0x20` 置位显示「疑似锁定」，仅 `0x80` 置位显示「疑似已解锁」，两个位都置位显示「线索冲突」，都清零显示「未知」。其他状态参考公开逆向研究。字段超过 20 秒没有更新时保留原值并标记过期，当前状态显示为未知。 [论文图 8、表 9 和表 16](https://crysp.petsymposium.org/popets/2020/popets-2020-0003.pdf)

## 观察步骤

1. 在「手表管理」确认当前设备是你的 Apple Watch。绑定后由后台服务自动开始检测。
2. 应用将 Windows 提供的实时 BLE 广播地址与已配对设备地址比较。在场状态、最近匹配时间、RSSI 和漏检测量来自收到的匹配广播。Windows 蓝牙连接状态单独显示。
3. 在「漏检测量」选定亮屏且已解锁、熄屏且已解锁、重新锁定或重启后尚未解锁。保持手表在同一位置和所选状态，运行一轮 60 秒。每个完整的 10 秒窗口内没有匹配即计为一次漏检。停止扫描、取消本轮或监测中断会丢弃未完成的轮次。
4. 对每种状态重复测量。应用保留本次运行最近 200 个完整轮次，可复制每个窗口的匹配次数、最后匹配时间和 RSSI。想检查误匹配时，可让手表离开接收范围，在「在场检测」观察匹配次数是否停止增加。

结果取决于 Windows 蓝牙栈如何把已配对设备的广播地址交给应用。公开接口可能只提供解析后的身份地址，因此仅凭该地址保持不变无法确认空中随机地址何时轮换。RSSI 用作观测指标，无法验证佩戴、解锁或实际距离。

## 手表靠近时亮屏并使用 Windows Hello

1. 在 Windows 登录选项中设置 Windows Hello 人脸识别。如果有自动关闭锁屏界面的选项，请将其打开。
2. 在「手表管理」开启「手表靠近时亮屏，使用 Windows Hello」，并开始检测手表。可以关闭设置窗口让应用留在托盘。应用会记住所选模式，开始检测后恢复亮屏功能。开启此模式会关闭密码式自动解锁。
3. 使用 `Win+L` 锁屏。等待显示器熄屏，在接收范围外停留至少 15 秒，然后带手表返回并面向摄像头。电脑需要保持运行。进入睡眠、休眠、注销或重启后，应用无法以这条路径请求亮屏。
4. 对照测试时，锁屏后让手表留在近处；随后让手表离开并返回，但不看向摄像头。检查显示状态和 Windows 是否仍要求身份验证。

匹配的广播达到 −65 dBm 才作为接近信号。锁屏后至少连续 15 秒没有近信号，再观察到相隔不超过 4 秒的两个新近信号，应用才会在本机控制台熄屏时用 `ES_DISPLAY_REQUIRED` 发出一次亮屏请求。扫描停顿后重新计时。普通匹配和已经开启的 IRK 观测都可提供信号。阈值和等待时间是原型默认值，受手表广播频率和环境影响。

诊断日志中的 `watch_wake_requested` 记录请求结果，`watch_wake_display_on` 记录随后观察到的显示状态变化，`watch_wake_session_unlocked` 记录 30 秒内收到的 Windows 会话解锁通知。会话通知无法区分人脸、PIN 或其他 Windows 登录方式。Windows Hello 在某些情况下会要求手动进入登录界面，例如刚手动锁屏后的人脸识别防抖期。

亮屏模式只使用蓝牙触发显示请求，由 Windows Hello 验证身份，无需提供账户密码。已绑定手表仍可用于自动锁定。以前保存的密码会保留，如需移除，可在「手表管理」移除手表。

- [微软 Windows Hello 人脸认证](https://learn.microsoft.com/windows-hardware/design/device-experiences/windows-hello-face-authentication)
- [微软关于靠近唤醒和人脸识别防抖的说明](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/sensors-presence-wake-on-approach)
- [微软亮屏请求 API](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate)
- [微软 Windows 凭据流程](https://learn.microsoft.com/en-us/windows-server/security/windows-authentication/credentials-processes-in-windows-authentication)
- [微软 Windows Hello 配套设备框架](https://learn.microsoft.com/en-us/windows/uwp/security/companion-device-unlock)
- [Apple 外围服务约束](https://developer.apple.com/documentation/CoreBluetooth/CBPeripheralManager)和 [watchOS 蓝牙后台运行规则](https://developer.apple.com/documentation/WatchKit/using-background-tasks)
- [Sysinternals Autologon](https://learn.microsoft.com/en-us/sysinternals/downloads/autologon)

## Apple Watch 自动解锁（实验功能）

ZuUnlock 的作者确认它使用自定义 Windows Credential Provider，并要求用户配置账户密码。UnlockPC 采用同一 Windows 接口，沿用自己的 IRK 检测与接近判定。手表返回时，登录组件通过 Windows Negotiate 认证包提交账户凭据。Windows 决定认证结果。首版支持 Windows 11 x64 的本机账户和已登录、已锁定的本机控制台会话。 [ZuUnlock 官网](https://zu.3gxk.net/) · [作者说明](https://www.v2ex.com/t/1172240) · [微软自动提交接口](https://learn.microsoft.com/windows/win32/api/credentialprovider/nf-credentialprovider-icredentialprovider-getcredentialcount)

打开 UnlockPC，在「手表管理」完成手表绑定，再开启密码式自动解锁并点击「设置解锁凭据」。首次开启时，Windows 会请求管理员确认，UnlockPC 随后自动安装登录组件。安装完成后，在 Windows 凭据窗口输入当前本机账户密码。

「手表管理」可分别开启自动解锁和自动锁定。自动锁定默认关闭。离开由持续弱信号确认。最近 18 秒内，若最新包达到 −85 dBm 或更弱，且两条这样的包相隔至少 3 秒，即可确认离开。否则需要三条 −75 dBm 或更弱的包，首尾相隔至少 6 秒。较强的包或超过 12 秒的包间隔会清除累计。自动锁屏还要求最新弱包在 5 秒内且本机已连续 15 秒无输入。长时间没有广播只会让旧样本过期。

输入检测结合 Windows 会话输入、后台 Raw Input 的 HID 报告变化，以及游戏控制器状态。游戏手柄的按钮、摇杆或扳机保持在操作位置时会持续计为活动。通用 HID 设备连续发送完全相同的报告时，只记录首次收到的报告。

手动锁屏后先等待手表离开。自动锁屏后或 Windows 从睡眠或休眠恢复且仍锁屏时，立即等待手表返回。默认收到一条 2 秒内、信号达到 −50 dBm 或更强的 IRK 匹配广播即可发起一次认证。最低 RSSI、统计窗口和所需广播数仅可在开发版调试页调整。启用手表解锁线索开关后，每条计入解锁规则的广播都必须带有 Nearby Info 数据标志，且 `0x80` 置位、`0x20` 清零。系统从睡眠恢复时只接收恢复后采到的广播。凭据领取窗口为 12 秒。

UnlockPC 用当前用户范围的 Windows DPAPI 保存绑定设备和可选的账户凭据。旧绑定在首次读取时迁移，去除应用文件里的 IRK 副本。只启用自动锁定时可以直接绑定手表，无需输入账户密码。以同一 Windows 用户启动 UnlockPC 时，应用会自动选择已绑定的手表，校验自动解锁需要的凭据并恢复检测。手表管理的开关分别停用对应动作。移除手表或解除 Windows 蓝牙配对会删除保存的配置。Windows 密码变更后，可用「修改密码」更新。

实验功能无法判断手表是否佩戴、是否已经解锁，也无法区分有效广播与重放或中继。IRK 只用于识别广播地址。PIN、密码和 Windows Hello 等系统登录入口照常保留。自动解锁只适用于已登录的本机控制台。Windows 决定何时加载第三方登录组件。

日志中的 `direct_unlock_requested` 表示已创建临时请求，`direct_unlock_credential_sent` 表示登录组件的凭据传输完成，`direct_unlock_session_unlocked` 表示请求后的 30 秒内收到会话解锁通知。通知本身不能确认使用了哪种认证方式。

登录组件将认证阶段和返回码写入 Windows「应用程序」事件日志。用 `logonUiPid` 和 `attempt` 对照同一次提交。`credential_received` 表示登录组件已读到凭据，`credential_serialized` 表示已将封装结果返回给 LogonUI，`authentication_result` 表示收到 Windows 的 `ReportResult` 回调并记录 `status`、`subStatus`。如果没有回调记录，仅凭会话解锁通知无法判断本次提交的结果。

```powershell
Get-WinEvent -LogName Application -FilterXPath "*[System[Provider[@Name='UnlockPC Credential Provider'] and EventID=100]]" -MaxEvents 30 |
    Select-Object TimeCreated, @{Name='Details';Expression={$_.Properties[0].Value}}
```

已有安装可在管理员 PowerShell 中运行 `pwsh -File scripts/Set-CredentialProvider.ps1 -Action Install` 更新。注册会指向新 DLL，下一次 LogonUI 加载组件时使用新版本。

要注销该组件，从 64 位管理员 PowerShell 运行下列命令。安装文件留在 Program Files，避免删除仍被 LogonUI 加载的 DLL。

```powershell
pwsh -File scripts/Set-CredentialProvider.ps1 -Action Uninstall
```

## IRK 换址验证

绑定手表后，打开在场检测的「IRK 换址验证」。UnlockPC 会按需启动 IRK 服务。界面的普通匹配量来自 Windows 地址，IRK 命中量来自服务对随机广播地址的解析。

同一轮里出现至少两个不同的 IRK 命中地址后，再让目标手表离场、留另一块手表在附近，观察 IRK 命中计数是否停止增长。日志 `scan_irk_address_resolved` 对每个新命中地址记录编号、当时是否等于 Windows 配对地址，以及是否带有 Apple 厂商数据。仅出现一个地址时，跨换址识别仍待验证。
