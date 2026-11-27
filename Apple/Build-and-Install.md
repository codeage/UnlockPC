# UnlockPC iPhone 与 Apple Watch 编译安装

两个 App 的显示名都是 **UnlockPC**，共用一个 Xcode 项目。GitHub Actions 用 macOS 和 Xcode 构建同一个 Release IPA，Windows 下载后按分发渠道签名。直装经配对的 iPhone 把 App 安装到手表。TestFlight 由 Apple 分发，测试者在 iPhone 的 TestFlight 中安装两个 App。

| Target / Scheme | Bundle ID | 最低系统 | IPA 内位置 |
| --- | --- | --- | --- |
| `UnlockPC` | `com.bytereset.unlockpc` | iOS 16.0 | `Payload/UnlockPC.app` |
| `UnlockPC Watch` | `com.bytereset.unlockpc.watchkitapp` | watchOS 9.2 | `Payload/UnlockPC.app/Watch/UnlockPC Watch.app` |

构建和分发使用外层的 `UnlockPC` scheme，由它编译并嵌入手表 App。`UnlockPC Watch` scheme 可用于 Xcode 单独运行手表 App。

| | Ad Hoc 直装 | TestFlight 内测 |
| --- | --- | --- |
| 描述文件 | 两个 App 各一份 Ad Hoc 描述文件 | 两个 App 各一份 App Store Connect 分发描述文件 |
| 设备 | iPhone、Apple Watch 都要在团队登记，启用开发者模式 | 使用获邀账号，无需登记设备 UDID 或启用开发者模式 |
| 安装入口 | Windows 安装脚本，通过配对的 iPhone | 配对 iPhone 上的 TestFlight |
| 安装确认 | 分别从 iPhone 和手表读回构建号 | 分别核对 TestFlight 的 iPhone 和 Apple Watch 安装状态 |

[Apple Ad Hoc 描述文件](https://developer.apple.com/help/account/provisioning-profiles/create-an-ad-hoc-provisioning-profile/) · [Apple 开发者模式说明](https://developer.apple.com/documentation/xcode/enabling-developer-mode-on-a-device) · [TestFlight 安装说明](https://testflight.apple.com/#installation)

## 1. 在 GitHub 构建 Release IPA

1. 提交并推送本次拟编译的 Apple 源码，确认提交在 `codeage/UnlockPC` 上。修改 `Apple/**` 或构建工作流并推送到 `codex/unlockpc-apple-build` 会自动触发。已推送的分支也可以在 Actions 手动运行，选择含目标提交的分支。
2. 在 Actions 核对运行对应的提交 SHA 和结果。每次新运行的 `run_number` 写入两个 App 的 `CFBundleVersion`，`CFBundleShortVersionString` 来自 Xcode 项目的 `MARKETING_VERSION`。GitHub 运行 ID 用来下载产物，与 App 构建号不同。重新运行同一条 Actions 记录会沿用原构建号。已被 App Store Connect 接受的版本如果要再次上传，使用新的构建号。
3. 下载 `unlockpc-unsigned-device-build` 产物。保留原始 `UnlockPC-unsigned.ipa`，两条分发路线都从它开始。当前工作流使用 `macos-26`、`Release`、`generic/platform=iOS` 和 `CODE_SIGNING_ALLOWED=NO`。工作流会核对两个 Bundle ID、可执行文件和 IPA 完整性，产物保留 7 天。 [GitHub 构建工作流](../.github/workflows/build-apple.yml)

从仓库根目录运行下列 PowerShell 命令。这里用 Build 17 的运行 ID 示范，下一次换成实际运行 ID；过期的产物需用保存的本地副本或重新构建。

```powershell
$runId = 45951973704
$artifacts = Join-Path $env:LOCALAPPDATA "ByteReset/UnlockPC/signing/releases/$runId"
gh run view $runId -R codeage/UnlockPC --json conclusion,headSha,number
gh run download $runId -R codeage/UnlockPC --name unlockpc-unsigned-device-build --dir $artifacts
```

## 2. Ad Hoc 直装

### 签名

1. 在 Apple Developer 核对两个显式 App ID 和目标设备。为 iPhone App 和 Watch App 各准备一份有效 Ad Hoc 描述文件，按各目标选择已登记的设备，并关联同一张 Apple Distribution 证书。Windows 当前用户证书存储中应有该证书及其可导出的私钥。能力变更后重新生成受影响的描述文件。当前 Watch App 使用 Push Notifications 和 Time Sensitive Notifications。 [Apple Ad Hoc 描述文件](https://developer.apple.com/help/account/provisioning-profiles/create-an-ad-hoc-provisioning-profile/)
2. 在有现成签名工具的 Windows 上运行 `Sign-APNsAdHoc.ps1`。脚本从本机证书存储和配置目录读取证书及两份描述文件，通过 `zsign 1.1.3` 签入 iPhone 和 Watch 两层，再用 `Restore-IpaModes.py` 从原始 ZIP 恢复可执行权限。当前脚本查找的描述文件名是 `ByteReset_UnlockPC_AdHoc.mobileprovision` 和 `ByteReset_UnlockPC_Watch_APNs_AdHoc.mobileprovision`。

```powershell
$signing = Join-Path $env:LOCALAPPDATA 'ByteReset/UnlockPC/signing'
$unsignedIpa = Join-Path $artifacts 'UnlockPC-unsigned.ipa'
$adHocIpa = Join-Path $artifacts 'UnlockPC-AdHoc.ipa'
pwsh -File "$signing/Sign-APNsAdHoc.ps1" -InputIpa $unsignedIpa -OutputIpa $adHocIpa
```

签名脚本、描述文件和设备配对资料由签名机本地保管。仓库提供构建工作流。本地脚本使用 PowerShell 7、Python、`zsign` 及预配置的证书和设备。换电脑或换测试设备时，按目标团队更新本地路径、证书、描述文件和设备配置。签名命令要求输出文件尚未存在。

### 安装与核对

1. 将 Apple Watch 与目标 iPhone 配对，在两台设备上启用开发者模式并解锁。让 iPhone 信任这台 Windows 电脑，确认已经建立可用于本地 Wi-Fi 安装的配对。连接不到 iPhone 时，先核对信任、网络及实际 IP。 [Apple 开发者模式说明](https://developer.apple.com/documentation/xcode/enabling-developer-mode-on-a-device)
2. 用 Windows 的 `Install-APNsAdHoc.py` 安装。下方使用现有签名机缓存的 `pymobiledevice3` wheel；也可以使用已装好该库及其依赖的 Python 环境。将示例 IP 和构建号替换为本次实际值。安装器会校验 IPA、两个 Bundle ID 和嵌入的描述文件，且仅连接本地配置的可信设备。

```powershell
$buildNumber = '17'
$phoneAddress = '192.0.2.10'
$wheel = Join-Path $env:LOCALAPPDATA 'ByteReset/Metronome/wireless/pymobiledevice3-11.64.0-py3-none-any.whl'
$env:PYTHONPATH = $wheel
py -3 "$signing/Install-APNsAdHoc.py" $adHocIpa $buildNumber $phoneAddress
```

3. 脚本先安装或核对 iPhone App，再经 iPhone 的 `CompanionProxyService` 查询手表。如果自动安装已完成，就核对版本；若手表仍是旧版，则从签名 IPA 提取嵌入的 Watch App，经转发的手表安装服务补装。自动安装和补装同时进行时，等待自动安装完成后重新读回。
4. 以脚本分别输出 `Verified iPhone` 和 `Verified Apple Watch` 且构建号正确为准。安装结束后退出安装会话。测通知或触感延迟时，从手表上手动打开 Release 版，保持调试器和诊断连接断开。iPhone 在这条路线用于安装桥接，手表 App 的本地 BLE 提醒由手表处理。

## 3. TestFlight 内测

### 签名和验包

1. 从同一个未签名 IPA 开始。核对 UnlockPC 的 App Store Connect 记录，iOS Bundle ID 为 `com.bytereset.unlockpc`，SKU 为 `UnlockPC-Watch`。外层 iPhone IPA 包含手表 App，一次上传会带上两个 App。 [UnlockPC 的 App Store Connect 记录](https://appstoreconnect.apple.com/apps/6839917467/testflight) · [Apple watchOS App 记录说明](https://developer.apple.com/help/app-store-connect/create-an-app-record/add-watchos-app-information/)
2. 给两个 Bundle ID 各准备一份有效的 App Store Connect 分发描述文件，关联同一张 Apple Distribution 证书。在 Windows 解开原始 IPA，把两份描述文件分别复制为对应 App 根目录下的 `embedded.mobileprovision`。从已验证的描述文件读取各自的 `Entitlements`，将默认通配的 `keychain-access-groups` 展开为对应的 `TEAMID.BundleID`，分别保存为 `entitlements-0.plist` 和 `entitlements-1.plist`。确认分发签名中 `get-task-allow=false`，Watch 的 `aps-environment` 和 `com.apple.developer.usernotifications.time-sensitive` 与分发描述文件一致。 [Apple App Store Connect 描述文件](https://developer.apple.com/help/account/provisioning-profiles/create-an-app-store-provisioning-profile/)
3. Build 17 验证通过的是 Windows 版 `rcodesign 0.29.0`。在签名机临时目录将 Apple Distribution 叶证书及私钥导出为临时 PKCS#12（`EndEntityCertOnly`、`TripleDES_SHA1`），单独提供与证书链匹配的 WWDR G3 DER 证书。用临时密码文件传递密码，签完清理临时私钥和密码文件。在包含 `Payload`、两个权限 plist 和证书文件的签名工作目录运行以下命令。Windows 上 Watch 权限的路径选择器使用反斜杠。 [rcodesign 0.29.0](https://github.com/indygreg/apple-platform-rs/releases/tag/apple-codesign%2F0.29.0)

```powershell
rcodesign.exe sign --p12-file distribution.p12 --p12-password-file pfx-password `
  --certificate-der-file wwdr.cer --timestamp-url none `
  -e entitlements-0.plist -e 'Watch\UnlockPC Watch.app:entitlements-1.plist' 'Payload/UnlockPC.app'
```

4. 将签好的 `Payload` 重新打成 IPA。Windows 重打包时可用 `Restore-IpaModes.py <原始 IPA> <已签名的中间 IPA> <最终 IPA>` 恢复原始 ZIP 的执行权限，不改已经签名的内容。用 macOS 解开最终 IPA 后，核对所有架构的签名和 Watch 权限。Build 17 的 `zsign` 包曾在 Apple 验包报 `90035`，默认架构验签通过仍可能漏掉 Watch 的另一个架构。另一次上传报 `90166`，用 Windows 路径选择器把 entitlement 写到嵌套 Watch bundle 后通过。

```bash
codesign --verify --all-architectures --strict --deep 'Payload/UnlockPC.app'
codesign --verify --all-architectures --strict 'Payload/UnlockPC.app/Watch/UnlockPC Watch.app'
codesign -d --entitlements :- 'Payload/UnlockPC.app/Watch/UnlockPC Watch.app'
```

### 上传、分配和安装

1. 用 App Store Connect API 上传，或者在 Mac 上用 Apple Transporter 上传签好的 IPA。Windows 的 API 路线使用 ASC API 密钥签发 JWT。在目标 App 下创建 `buildUploads`，用 `buildUploadFiles` 预留 IPA，按返回的 `uploadOperations` 上传对应数据范围，提交文件及其校验和。上传成功后，等 App Store Connect 的处理状态变为 `VALID`。 [Apple 上传构建](https://developer.apple.com/help/app-store-connect/manage-builds/upload-builds) · [Build Uploads API](https://developer.apple.com/documentation/appstoreconnectapi/build-uploads)
2. 在 TestFlight 完成该构建的出口合规声明，再分配到 `UnlockPC Watch Internal` 内测组。Build 17 已确认该构建「不使用非豁免加密」，读回值为 `usesNonExemptEncryption=false`。后续构建按该版本的实现和依赖重新核对声明。写入前核对目标 App、版本、构建号及内测组，获批后操作并读回处理状态、合规值和内测组成员。内测分配和 App Store 审核是两项操作。 [Apple 出口合规说明](https://developer.apple.com/help/app-store-connect/test-a-beta-version/provide-export-compliance-information-for-beta-builds/) · [Apple 内测组说明](https://developer.apple.com/help/app-store-connect/test-a-beta-version/add-internal-testers/)
3. 在与手表配对的 iPhone 上打开内测邀请，在 TestFlight 接受邀请并安装或更新 UnlockPC。打开 TestFlight 的 UnlockPC 详情，在「信息」下找到 Apple Watch 区域，再点「安装」或「更新」。安装完成后确认 Apple Watch 区域显示「打开」，并在手表上打开 UnlockPC。 [Apple Watch TestFlight 安装说明](https://testflight.apple.com/#installation)
4. 若从同构建号的 Ad Hoc 版切换，确认配对 iPhone 上的 TestFlight 确实完成了 iPhone 和 Watch 两处安装或更新。单凭系统读到的版本号、iPhone 显示已装，或手表上已有图标，无法确认 Watch 已切换到 TestFlight。

## 已验证的构建

**0.1.0 (17)** 的构建使用提交 `dbd253af52d52da96fb6e134a0c151a4752e341c`。两条路线使用同一份未签名 Release IPA。 [Actions 运行 45951973704](https://github.com/codeage/UnlockPC/actions/runs/45951973704)

- **Ad Hoc** 已从 iPhone 和 Apple Watch 分别读回 `0.1.0 (17)`。分发签名已核对，测试前已断开安装转发和调试连接。
- **TestFlight** 构建已通过 Apple 处理，出口合规已保存，构建状态为 `IN_BETA_TESTING`，已分配给 `UnlockPC Watch Internal`。这是服务端确认记录，手表端 TestFlight 安装待验证。
