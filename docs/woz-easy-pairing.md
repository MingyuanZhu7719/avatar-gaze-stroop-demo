# WoZ Photon 房间连接

两台电脑都需联网，使用同一版、已配置的 Participant / Operator 完整程序包。无需同一路由器，也无需填写 IP、端口或 clock ID。先分别打开 SteamVR，确认头显和眼动客户端可用。

Vive-Study 参与者版输入匿名 ID 后，Ink colour、Word meaning、Visual search 均可选择 WoZ（需要启用 WoZ 的完整包）。新版同步协议包含背包布局和目标预览／搜索阶段，与旧版不兼容；必须同时更新两端。参与者眼动缺失会记录在本地数据中并允许任务继续；这不取消实验者源、手柄、头部追踪和网络就绪要求。

1. 实验者端打开头显和眼动，点击 **Create room**。
2. 参与者选择 WoZ，完成 tutorial / practice。在正式实验准备页填写房间码，点击 **Join room**。
3. 核对对应参与者电脑显示同一房间码。实验者点击 **Accept this participant**。
4. 实验者确认转发权限；如果本次有参与者本地录制，也需明确同意该存储范围，然后点击 **Connect to participant**。
5. 参与者端自动完成连接；追踪就绪后再点击 **Begin current block**。

房间码仅用于这次连接。断开、退出或重启后重新创建房间；已停止的实验不会自动恢复。连接不代替参与者的录制和头像共享选择。

## 传输内容

Photon 通过加密的互联网连接（WSS）中转配对信息以及已获准的头部／视线和任务显示状态。Photon 是第三方云中转，并非端到端加密的本地直连。参与者的原始眼动、答案、reaction time、问卷和 CSV 文件保留在本机，不发给实验者。

**Advanced: manual LAN connection** 保留旧的局域网手动备用流程；该通道经过认证但未加密。一次会话只使用一种连接方式。双头显实机同步、延迟及眼动精度仍需独立验收。

## 无法连接时

确认两端是匹配版本、固定地区一致、可访问互联网，房间码输入正确。重新创建房间后重试。实验者端点击 **Export debug ZIP** 导出诊断，见[排错指南](woz-operator-debugging.md)。程序不会自动修改防火墙、安装证书或改变全局 XR 设置。

## 开发与打包

Photon Realtime Unity SDK 固定为 **5.1.21**。专有 SDK 不提交 Git，开发者需从[官方 SDK 页面](https://doc.photonengine.com/realtime/v5/getting-started/sdk-download)登录下载 Unity ZIP，并下载[官方 OSS notices](https://doc.photonengine.com/docs/content/oss-.net_client_sdks.pdf)。执行：

```powershell
python scripts/photon-sdk.py --archive <photon-unity-sdk_v5-1-21.zip> --oss-notices <oss-.net_client_sdks.pdf>
python scripts/photon-sdk.py --verify
```

导入器校验固定 SHA256，仅导入 Realtime 源码与 PhotonClient release DLL 到被忽略的 `Assets/Photon`；不导入 Chat 或示例资源。源码快照包含 SDK 文件及哈希清单。Mono 回归使用同一 SDK，SDK 和应用分别编译。

构建脚本可用 `--photon-config <private-json>` 将私有配置复制到 player 旁。文件名为 `photon-woz.local.json`，仅含 `appId`（Realtime GUID）和 `region`（两端一致；当前开发测试配置为 `hk`）。此文件被 Git 忽略，诊断不得输出 App ID。参与者的配置位于 `UnityPlayer` 目录，实验者的配置与 exe 同级。App ID 随客户端分发，不是身份认证秘密；房间加入仍需匹配流程确认。请保留完整程序包和 `Photon-notices` 许可证文件。

构建不会自动连接网络。合成数据联通通过后，还需两台电脑、两个 Vive 的实际联合测试。

## 时序与验收

Photon 云通道采用固定 **750 ms** 的原始任务／控制证明有效期，旧的手动 LAN 通道保持参与者端 **150 ms**、实验者端 **100 ms** 的原有证明门槛。有效期从原任务发出时计算，收到较晚的消息不会重新开始计时。它是控制有效期上限，不表示实际视线延迟就是 750 ms。每个新转发样本仍需在本地源观测后 **100 ms** 内发送；实际姿态消息收到后在本地的驻留时间不超过 **150 ms**。

750 ms 的依据是两次约 300 ms 的请求／响应周期加 150 ms 余量：首次取得证明已用掉一个往返周期，而单个未决续期请求还需再等一个周期。开发测试曾出现连续 260／328 ms 的周期，两者合计超过原先的 500 ms 有效期。

**50 ms 是再次请求任务续期的最短间隔**；必须等待前一次确认，实际续期频率取决于网络往返时间，不能据此声称云端以 20 Hz 更新任务。获准的头部／视线姿态转发独立于任务续期继续，仍受姿态新鲜度和共享权限约束。

新版记录的 `woz_network_visual_budget_seconds` 标明本次通道使用的有效期；`woz_task_roundtrip_seconds` 记录相关原任务从参与者发出到参与者收到回调的时间，使用同一台电脑的单调时钟计算，不直接相减两台电脑的时间戳。CSV 中为可读秒数，旧版记录仍可读取。

开发机的香港区合成双客户端测试已观察到约 192–328 ms 的单次应用协议往返。这些数字仅说明开发测试中的网络条件，不是两台电脑、两个 Vive 的人体实验结果，也不保证未来延迟维持在该范围。

记录的接收时间是 Photon 回调处理时间，并非物理网络到达时间，也不是测量得到的端到端视线延迟。真人使用前仍需两台实际电脑共同测试延迟、断线停止、目标识别和眼动表现；不得根据合成联通结果声称眼动精度已通过。
