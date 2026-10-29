# PRD：VR Stroop Avatar Pilot

**版本：v0.1 · 2026-10-28 · 已获用户批准作为开发基线**

基线：[已发布实验方案 d9db8f8](https://github.com/MingyuanZhu7719/avatar-gaze-stroop-demo/blob/d9db8f84678f435b2cf62343ccbb717c5d256868/docs/experiment-design.md)。本稿将该方案及最新双端视图、负责人选模式、VR 内报错要求转为软件需求；不修改研究参数，不代表已有可运行软件或完成设备验证。SDK、通信和同步实现另写技术方案。

## 1. 产品目标

支持一次可核对、可重复执行的 pilot：呈现不同 avatar 注视条件，了解参与者的体验差异，同时记录行为、眼动和实际呈现过程。**被观察感的变化本身有研究价值；被评价感仍然测量，不要求其一定显著。**

软件成功的标准是流程正确、呈现与来源可追溯、故障可见、记录完整且能核对。主要研究对比及统计设置待定；本稿不把 RT 定为主要研究终点，不以显著性、预期方向或“某条件必须更有被评价感”作为软件验收条件。

## 2. 角色、设备和可见内容

采用两套 **VIVE Pro Eye、两台独立电脑、两个独立应用**。操作者应用采集并实时发送真实头部转动和眼睛注视；参与者应用接收后驱动参与者可见的操作者 avatar。自动模式由程序驱动同一 avatar。当前方向的实时控制需求已确认，链路尚未实测；反向显示参与者 avatar 不等于已确认反向同步其眼动。

| 角色或界面 | 已确认的职责 / 可见内容 | 边界 |
|---|---|---|
| 参与者 VR | 做 Stroop、按键作答、填写评分；正常主任务可见题目和操作者 avatar | 练习无 avatar；D 隐藏操作者 avatar；问卷和休息按基线隐藏 avatar。参与者不选择实验模式 |
| 操作者 VR | 可见参与者 avatar 和当前题目；WoZ 时按指定条件与节奏控制自己的头转和眼动 | 参与者 avatar 的驱动信号、姿态和眼动同步范围待定；是否显示正确率、答题结果或 RT 待定 |
| 实验负责人 | 配置会话，选择自动 / WoZ 模式，核对已分配的条件、顺序和 B 初始目标，确认开始；每组前锁定并记录配置 | 此处指 people in charge of the experiment，不是受试者；模式选择入口不能交给参与者，不能随意覆盖平衡分配 |
| 管理屏幕 / 管理入口 | 提供会话与流程控制、配置核对和数据导出入口 | 具体屏幕布局、是否另有 VR 管理入口及角色兼任方式待定；不预设新增账号系统 |

操作者侧在练习、D、问卷及休息阶段具体显示什么，以及参与者 avatar 的外观与几何映射，均需在界面设计中确认。已确认的参与者侧无-avatar规则保持不变。不得自动增加身体、手部或语音同步。

## 3. 实验流程和保留参数

| 状态 / 阶段 | 软件必须支持的行为 |
|---|---|
| 会话准备 | 建立研究会话 ID，核对版本、负责人配置、设备连接、头眼追踪、映射和双端题目对应关系；记录必要的筛查 / 同意状态。软件检查不等于研究审批 |
| 熟悉与校准 | 支持四种颜色、按钮映射和词义熟悉；分别检查参与者测量眼动与操作者控制眼动。校准与有效性标准待设备验证，不增加隐藏的练习通过门槛 |
| 练习 | 每次 8 题、参与者侧无 avatar，4 一致 + 4 不一致；只计首次反应，至少 6/8（75%）通过；最多两次尝试。错误后纠正并等待明确 Continue；纠正不改分数。两次均未过则结束标准任务 |
| 主任务准备 | 按已分配顺序进入下一组；负责人选择模式，核对并锁定已分配的条件、顺序及 B 初始目标，记录配置。何种模式用于哪个会话 / 组的分配规则待协议确认 |
| 主任务运行 | A、B、C、D 各一组；每组 24 题：12 一致 + 12 不一致，共 96 个计划试次。首次有效新按键即记录并推进；按键保持不能连答多题；无固定三秒等待、参与者侧无正确性反馈、不擅加作答 deadline |
| 组后评分 | 参与者侧隐藏操作者 avatar，依次采集五个独立的 1–5 分项目：专注、分心、被观察、被评价、负面评价担忧。各条件同措辞、同顺序；沿用[基线问项及锚点](https://github.com/MingyuanZhu7719/avatar-gaze-stroop-demo/blob/d9db8f84678f435b2cf62343ccbb717c5d256868/docs/experiment-design.md#8-post-block-questionnaire)，不合并总分 |
| 休息与下一组 | 评分后额外至少 30 秒中性休息，不把评分时间算入；参与者可延长，达到最低时长后才允许 Ready。第四组也先完成评分和休息，再进入结束流程 |
| 显式暂停 / 退出 | 保留参与者暂停、退出及操作者安全停止入口。显式暂停按基线在参与者侧隐藏题目和操作者 avatar、冻结任务与活动注视时钟；中断试次不记作答错误。主动恢复遵守下一计划试次、不静默重做原则；追踪 / 网络故障的自动暂停与重连恢复规则另列待定 |
| 结束与导出 | 区分正常完成、练习未过、退出和技术结束；保留实际完成情况与中断记录，按获批数据安排导出，不补造未完成试次 |

题目与顺序继续按基线生成：RED / GREEN / BLUE / YELLOW；四种一致词色组合各出现三次，十二种不一致组合各出现一次，每词和每色各六次。使用相同多重集的四份受约束顺序，避免完全相同词色刺激紧邻重复，并记录列表 ID / seed。Williams 顺序为 **ABDC、BCAD、CDBA、DACB**，B 的初始注视目标独立平衡；不能把 D 固定放第一组，不能按表现加题。48 题/组不是本期基线。

## 4. 两种控制模式

| 模式 | 控制来源 | 必须共用的部分 |
|---|---|---|
| 自动 | 参与者应用的程序源生成指定头眼运动 | 同一参与者可见 avatar、呈现接口、Stroop 流程、问卷和日志结构 |
| 真实头眼 WoZ | 独立操作者电脑 / 应用采集其 VIVE Pro Eye 的真实头转和眼动，经实时传输在参与者端映射 | 共用部分不代表轨迹、延迟或实际执行精度相同；WoZ 不能被按钮切目标或预录自动轨迹替代 |

- A 朝参与者、B 交替、C 朝任务、D 隐藏 avatar。B 的规定节奏仍为 **3.0 秒停留 + 0.5 秒转动，计划周期 7.0 秒**；自动模式按参考轨迹执行，操作者按该规则操作，实际偏差单独记录。
- 操作者 avatar 的身体保持固定。操作者真实头眼信号不能被强制重定时成自动轨迹，不能静默切换、混合或回退到自动源。参与者眼动、错误或反应速度不用于选择注视目标，也不用于提示操作者改变规则。
- 模式来源与变更必须可追溯；运行中切换模式不属于已确认流程。两种模式的存在不自动扩展为八个研究条件。

## 5. 故障与中断

| 事件 | 已确认的软件要求 | 尚待确认 |
|---|---|---|
| 头部或眼动追踪中断 / 失效 | 必须在 VR 内明确报错，不能只写桌面日志；区分头、眼有效性，记录来源、发生 / 检出 / 恢复事件和受影响阶段；失效数据不能标为有效 | 哪端或是否双端提示、检出阈值、提示文案 / 持续时间；是否自动暂停、隐藏 / 保持哪些内容及恢复流程 |
| 网络中断 / 失联 | 必须在 VR 内明确报错，记录连接状态、数据新鲜度和故障时间；不得静默改成自动控制或把旧样本伪装成当前有效输入 | 超时与重连判定、是否自动暂停、谁确认恢复、恢复到哪个流程 / 试次及如何处理故障期间数据 |
| 主动暂停 / 安全停止 | 采用第3节的显式暂停要求，留下可核对的时钟、状态和中断记录 | 具体按钮映射及确认界面在界面 / 技术设计中确定 |

**VR 报错不自动等同于暂停策略已经确定。** 自动暂停与故障恢复待定，不得由开发者静默补成默认行为；其最终规则须与基线“不静默重复中断题、不计为作答错误”的原则对齐。错误提示的位置和内容也需审阅，不能未经确认附带成绩反馈。

## 6. 数据与可核对性

各数据流须能通过会话、组、试次 / 片段 ID 关联；采用去标识研究编号，保留协议和软件版本。身份对应表、访问权限、保留与删除安排待确认，不宣称完全匿名。可导出机器可读数据及字段说明，具体文件格式与存储技术另定。

| 数据流 | 最少保留内容 |
|---|---|
| 会话与流程 | 负责人配置、模式 / 实际来源、操作者与设备角色、顺序、B 初始目标、列表 / seed、练习次数、状态转换、完成 / 退出 / 中断原因 |
| 题目与行为 | 词、字体颜色、一致性、试次身份、实际呈现和首次反应时间、RT、正确性、有效性及中断标记；主任务不把正确性回馈给参与者 |
| 操作者控制与 avatar 呈现 | 真实头转 / 眼动、分别的有效性、采样来源和时间；预定目标 / 时刻、实际接收 / 映射 / 呈现状态、停留 / 转动和偏差 |
| 参与者眼动 | 独立的参与者来源、时间、有效性、校准结果及 AOI 分类；不得用操作者眼动替代。AOI / 无效与 D 条件不适用的处理沿用基线 |
| 传输与同步 | 源采集、发送、接收和呈现时间及钟源定义；时钟映射、偏移 / 漂移和不确定性；可测延迟、丢包、失序、陈旧样本及追踪 / 网络故障 |
| 体验与休息 | 五项原始评分、问项版本和语言、评分 / 实际休息时长、Ready、暂停和移除头显事件；可选归因题是否纳入待定 |

同步行为与眼动意味着时间轴应可对齐和核查，不表示已达到某个精度。具体同步算法、采样率与性能阈值需在技术方案中确定并实测；模拟输入必须明确标记，不能混入真实采集数据。

## 7. 软件验收清单

以可重复的测试会话和导出记录验收。下列属于软件功能检查，不要求参与者产生预定心理效果或统计显著结果；涉及待定项的最终通过标准须先补齐。

| 编号 | 可核对的通过要求 |
|---|---|
| AC-01 角色与视图 | 验证参与者看到题目及操作者 avatar、操作者看到参与者 avatar 和同一当前题目；参与者侧练习、D、问卷及休息符合隐藏规则；不会把参与者 avatar 显示误当作其眼动同步已实现 |
| AC-02 模式权限 | 负责人能在每组前选择模式，核对并锁定已分配的条件、顺序及 B 初始目标，记录可查；受试者不能选择模式，不能随意覆盖平衡分配。不能仅因是 WoZ 操作者就默认其拥有负责人权限 |
| AC-03 完整流程 | 测试练习通过、一次重试通过、两次未过、正常四组、延长休息、主动暂停和退出；题量、评分顺序及状态记录均与第3节一致 |
| AC-04 题目与行为 | 自动核对 24 题的组成和列表约束；核对双端题目 / 试次身份。主任务首次新按键只推进一次、保持按键不连答、无固定三秒等待、参与者侧无正确性反馈；RT 及首次反应可从事件时间复核 |
| AC-05 控制来源 | 自动源与真实操作者源分别通过同一呈现接口；能从日志核对实际来源和 avatar 输出。自动参考时间可检查，人工实际轨迹和偏差保留，不以强制同轨迹冒充通过 |
| AC-06 VR 故障可见 | 分别注入头 / 眼追踪失效及网络中断，确认 VR 内出现可识别报错且日志对应；无静默自动回退或伪造有效样本。提示端、时限及暂停 / 恢复行为按待定项冻结后验收 |
| AC-07 数据联查 | 一次测试会话的行为、双方眼动、avatar、评分与异常可按身份和时间关联；缺失、失效、模式来源和中断可见；五项评分独立，无伪造完成记录 |
| AC-08 实机记录 | 在两台独立电脑与两套 VIVE Pro Eye 上验证双应用数据链及视图，记录实际延迟、同步误差、追踪有效性和恢复事件；数值门槛待定，桌面演示或截图配置不视为实机通过 |

## 8. 范围外与待定项

本期 PRD 不增加身体 / 手部 / 语音同步，不把 WoZ 改为基于参与者表现的反应式 avatar，不加入运行中模式切换、48题/组或额外研究条件，不选择 SDK / 引擎版本、网络协议或同步算法，也不将“研究显著”定义成软件成功。

| 类别 | 待确认内容 |
|---|---|
| 产品与交互 | 故障提示在哪端显示；追踪 / 网络失效是否自动暂停，重连后谁确认、如何恢复及题目落点；操作者能否看正确率 / 答题结果 / RT；参与者 avatar 的驱动和眼动同步范围；管理入口和非主任务阶段操作者视图 |
| 技术方案（单独形成） | 引擎 / SDK 版本、操作者电脑配置、通信协议和网络设置、坐标映射、采样与数据格式、同步方案、超时 / 追踪有效性和延迟等阈值、断线 / 失效安全策略及持久化实现 |
| 研究与采集准备 | 主要研究对比、样本量 / 精度、统计模型及阈值、RT 处理等未决参数；问卷语言和可选归因题；审批、筛查、校准 / AOI、数据保存与退出安排。保持待定，不在本 PRD 里补定或转写成软件成功指标 |

**批准状态：**用户已确认本稿的软件范围、双端视图与角色、流程、日志和可验收要求，并授权发布与分阶段开发。未决项继续保持待定；实现、测试及实机验证状态由开发记录单独报告。


## 9. Classroom presentation requirement (user clarification, 2026-10-29)

The participant presentation must place the existing coloured Stroop word on a world-space whiteboard in a simple classroom, with a standing observer avatar beside the board. The intended composition is a board to the left/centre and the observer to the right, visible together from the participant's normal viewing position. The reference is a spatial-composition example; its letter matrix is not a replacement task. The operator application continues to show the participant avatar and task according to the role and phase rules above.

Use restrained walls, a floor, and a board frame; avoid unnecessary classroom decorations, animated elements, or visual distractors. The avatar must not obscure the board. Maintain controllable head and eye orientation and derive gaze targets from the actual board/avatar geometry. The current repository contains only procedural primitives, so the first implementation may use a clearly described stylized standing figure; a photorealistic or humanoid-rigged asset is not part of this clarification.

Preserve the trial plan, words, ink colours, response mapping, correctness scoring, timing, prescribed gaze conditions, role separation, and real-experiment admission gates. Practice and condition D retain their no-observer rule; avatar body parts must follow the same visibility gate as the head and eyes. A neutral dark task inset within the light whiteboard surface may preserve the existing ink contrast. It must not become a new condition or change the task word size in response to the current trial.

Keep the accepted Quest instruction/status HUD at 2 m with its existing bounded angular layout. This is a HUD constraint, not a scale factor for the room. The task word belongs to the board, not the head-following HUD. Quest input remains preview-only, and the final VIVE trackpad colour mapping remains unselected. Scene/build checks and illustrative non-trial render fixtures are not headset or experiment acceptance; the revised classroom requires a separate wearer layout review.
