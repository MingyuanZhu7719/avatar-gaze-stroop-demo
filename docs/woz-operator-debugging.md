# WoZ 实验者程序：启动与导出报错

保留完整的 Operator 文件夹，双击 **Start-WoZ-Operator.cmd** 启动。先打开 SteamVR，确认头显可用，再在程序中点击 **Start headset view**。眼动功能仍需要本机的 HTC/SRanipal 软件与校准。

Participant 与 Operator 共用眼动安装查找：优先使用明确指定的 DLL，其次查找正在运行的 `sr_runtime.exe` 所在目录与 HTC `SOFTWARE\VIVE\SRWorks\SRanipal` 登记的安装位置，最后检查原默认目录。因此 E 盘等自定义安装不再需要修改代码。只使用已安装、版本为 **1.3.5.1 的 x64 SRanipal.dll**；HTC 运行时的版本号是另一回事。程序不下载或附带 HTC DLL。

如果 HTC 登记信息缺失，可以在启动程序旁新建 `sranipal-client-path.txt`，只写一行实际 `SRanipal.dll` 的完整路径。Participant 可放在外层 `AvatarStroopViveEyePreview.exe` 旁，也可放在 `UnityPlayer` 内的实际程序旁；Operator 放在其 EXE 旁。环境变量 `STROOP_SRANIPAL_DLL` 优先于该文件。明确指定的坏路径会直接报错，不会静默改用旧副本。只指定安装目录中的原文件，不要把 DLL 单独复制进程序包。

日志中的 `EYE_CLIENT_INSTALLATION`（Participant 为 `VIVE_EYE_CLIENT_INSTALLATION`）保存发现来源、选中路径和实际加载路径。诊断工具 `ViveEyeProbe.exe --check-installation` 仅检查位置、版本和架构；`--check-client-load` 额外检查 DLL 加载与入口函数，两者都不初始化眼动、不采集数据、不修改校准或同意设置。

启动器读取 `%LOCALAPPDATA%\openvr\openvrpaths.vrpath` 中登记的 SteamVR 安装位置，支持 E 盘等自定义目录；仅为本次程序设置 `XR_RUNTIME_JSON` 和 `VR_OVERRIDE`，让 OpenXR 与 OpenVR 使用同一套安装，不会修改系统默认 OpenXR runtime。请保留同目录的 `WoZOperatorLauncher.exe`。每次启动会在程序旁创建 `WoZLogs/run-*`，保存 `Player.log` 和 `Launcher.txt`；后者记录选定的 manifest、OpenXR 和 OpenVR 库路径。关闭程序后会自动打开该文件夹。

如果找不到登记的安装位置，先启动一次 SteamVR。也可以找到实际安装目录中的 `steamxr_win64.json`，将该文件拖到 **Start-WoZ-Operator.cmd** 上明确选择该安装。看到“已有程序运行”的提示时，先关闭旧的实验者程序；无法写入日志时，将完整 Operator 文件夹复制到桌面后重试。

双机连接使用[Photon 房间流程](woz-easy-pairing.md)：实验者点击 **Create room**，参与者输入房间码并 **Join room**；实验者核对后 **Accept this participant**，再确认转发权限并连接。两台电脑都需联网。

参与者加入房间后显示 **Task on hold** 可以是正常的开始前等待：加入房间不等于已完成连接，也不自动开始正式题目。Operator 连接并取得当前追踪状态后，仍由 Participant 电脑点击 **Begin current block**。A/B/C 的评价者头像需要 Operator 的有效头部和眼动；参与者眼动缺失可继续的规则不替代这一要求。连接期间保持 Operator 窗口在前台。

进入教室后，实验者站位位于参与者视角中白板的右侧，对应参与者所见的评价者头像位置。程序在首次有效头显追踪时放置教室，不需要等双机连接；Device 或 Floor 追踪原点均采用同一虚拟眼高，之后保留真实头部移动。它不会自动重设 SteamVR 的朝向；请转头面向参与者位置。连接时不会再次移动站位。

头显中央显示简短的准备提示，正式题目时让出中间视野。配对、权限和完整诊断仍在电脑上操作。尚未连接或参与者尚未进入可共享的正式题目时，不显示实时参与者头像。眼动启动后若 SteamVR 重置追踪原点，需重新打开程序建立新会话。

## 出现问题后

1. 点击 **Export debug ZIP**。等待出现 **Saved in logs folder: …zip** 后再关闭程序；显示 `Exporting diagnostics...` 时文件尚未完成。
2. 点击 **Open logs folder** 找到生成的 `WoZ-debug-*.zip`。正在向参与者转发数据时，此按钮禁用；导出 ZIP 仍可用。需要打开文件夹时，先停止转发。
3. 把 ZIP 发到此聊天，并说明刚才点击了哪个按钮、头显里看到什么。另一台电脑也采用相同步骤，无需寻找系统里的 `Player.log`。

**Copy error details** 可复制当前状态和错误摘要，适合先粘贴文字求助；**Show diagnostics** 显示完整错误、眼动状态及实际日志位置。导出失败时，先使用复制功能。

如果程序连界面都打不开，启动器关闭后打开的 `WoZLogs/run-*` 中仍可能有 `Launcher.txt` 和 `Player.log`；将本次文件夹压缩后发送即可。缺少 `Player.log` 也请保留 `Launcher.txt`。

## ZIP 内容

| 文件 | 内容 |
| --- | --- |
| `Summary.txt` | 程序版本、系统与显卡、OpenXR runtime、头显/眼动/连接状态和当前错误。 |
| `events.log` | 近期启动、追踪、连接及错误事件。 |
| `Player.log` | 当前 Unity 日志的近期内容；无法读取时记录原因。 |
| `Launcher.txt` | 启动器信息；使用启动器且文件可用时包含。 |

这些是实验者程序的诊断记录，不是参与者实验数据，也不替代参与者端的 raw/CSV。报告可能含本机路径、系统信息和局域网地址。日志是有长度限制的快照，建议问题发生后立即导出。该调试功能不代表双电脑、双头显 WoZ 已完成实机验收。
