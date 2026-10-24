---
name: asrgrab
description: 在 Windows 11 上把系统声音、麦克风或视频/音频文件里的对话逐字转写成文字并带说话人（角色）分离，输出 Markdown 与 JSON。全程本机离线运行（NAudio 加 sherpa-onnx），不依赖云端 API。适用于边看抖音或 B站或会议视频边抓取字幕、批量转写本地媒体、区分谁说了什么、本地隐私语音转写、把网页视频变成文字稿。当用户要求抓取视频字幕、转写视频对话、角色分离纪要、本地语音转写、隐私不联网转录时启用本技能。
agent_created: true
---

# asrgrab — 本地逐字转写与角色分离（AI Agent 优先）

asrgrab 是一个 Windows 11 原生的命令行工具，用 C# / .NET 8 编写，基于 NAudio（WASAPI 采集）
与 sherpa-onnx（离线 ASR + 说话人分离）实现。它在用户机器上把音视频内容实时或离线转成
带角色标签的文字稿，全程不上传任何服务器。

## 何时使用

- 用户在网页看抖音、B站、YouTube 等视频，想要逐字抓取对话并区分说话人；
- 需要把本地视频 / 音频文件转写成带时间轴、带角色的文字稿；
- 要求“本地、隐私、不联网”的语音转写（不调用云端识别 API）；
- 需要一份 JSON 结构化的转写结果供 Agent 继续处理（纪要、摘要、问答）。

不要用于：要求极高准确率且拒绝任何同音字/专名误差的场景（离线小模型会有机器速记误差），
或需要把转写结果实时回流到远端服务的场景。

## 安装到 WorkBuddy（技能安装）

本技能分两部分分发，请同时获取：

1. **技能包（本 zip）**：包含 `SKILL.md`、说明与辅助脚本，**不含可执行二进制**。
2. **运行时包 `asrgrab-runtime-win-x64.zip`**（**Windows 11 x64 专用**；win-x64 自包含，约 75MB，解压后约 180MB）：需另行获取并解压到技能目录的 `bin\`。

**安装步骤**

1. 安装技能包：从 SKILLHUB / 市场一键安装，或把本 zip 解压到任一技能目录：
   - 用户级（所有项目可用）：`%USERPROFILE%\.workbuddy\skills\asrgrab\`
   - 项目级（仅当前项目）：`<项目根>\.workbuddy\skills\asrgrab\`
2. 获取运行时：把 `asrgrab-runtime-win-x64.zip` 的内容解压到上述技能目录的 `bin\` 下，
   使 `bin\asrgrab.exe` 存在。（运行时包随本技能一同分发；若未随附，请从 Release 页获取：<https://github.com/chiangchenghsin-hash/asrgrab/releases/tag/v0.1.0>）
3. 验证：运行 `bin\asrgrab.exe doctor --human`，确认 `ok=true`。

安装完成后，WorkBuddy 在检测到相关诉求（见「何时使用」）时会自动加载本技能。

## 首次配置（开箱即用）

1. **系统要求**：Windows 11（x64）。运行时包已自带 .NET 8，**无需额外安装 .NET**。
2. **运行时已就位**：确认 `bin\asrgrab.exe` 存在（见上方安装步骤）。若缺失，先获取并解压 `asrgrab-runtime-win-x64.zip`。
3. **模型自动就绪（无需手动准备）**：转写所需的离线 ASR 模型由工具在**首次运行时自动准备**
   （仅首次需联网拉取本地模型，约 125MB，内置多镜像回退），准备完成即完全离线。
   无需事前任何操作；如需在首次转写前提前准备，可运行 `bin\asrgrab.exe models --download --human`。
4. **（可选）ffmpeg**：仅当需要用 `transcribe` 转写非 wav 的视频/音频时才需要。
   安装：`winget install ffmpeg` 或 `scoop install ffmpeg`。
5. **（可选）环境变量**：
   - `ASRGRAB_MODELS`：自定义模型目录（默认 `.\models`）。
   - `ASRGRAB_HOME`：自定义状态/会话目录（默认 `~\.asrgrab`）。
6. **（可选）Windows 隐私**：若使用「麦克风」等输入设备音源，请在
   `设置 → 隐私和安全性 → 麦克风` 中允许应用访问；`loopback`（系统声音）无需此项。

> 验证安装：运行 `bin\asrgrab.exe doctor --human`，确认 `ok=true` 且 `sources` 列出了音源、
> `asr_ready` 在模型准备就绪后为 `true`。

## 环境与运行

本技能的 Windows 原生二进制（仅适配 Windows 11 x64）以独立 **运行时包 `asrgrab-runtime-win-x64.zip`** 形式分发（不打包进技能 zip），
解压到 `bin\` 后即可运行，首次使用会自动准备模型。

1. 定位二进制：`bin/asrgrab.exe`（win-x64 自包含发布，由运行时包提供）。
2. 在任意工作目录运行（建议新建一个工作目录，模型与转录稿会落在那里）：
   `bin\asrgrab.exe doctor --human` 确认环境。

> 提示：转写较重，优先在用户机器上执行，不要在本会话沙箱里贸然跑全流程。
> 先跑 `doctor` 确认环境，再跑 `devices` 选音源。
> 注意 `bin/asrgrab.exe` 是相对技能目录的路径，调用时请使用绝对路径或先 `cd` 到合适工作目录。

## 输出约定（Agent 契约）

- 默认输出为 **JSON**：一次性命令输出单行 JSON；`start` / `transcribe` 流式输出 **JSONL**（每行一个事件）。
- 人类可读信息（进度、表格）走 **stderr**；加 `--human` 时 stderr 为主要输出，stdout 仍保持 JSON/JSONL 不污染。
- 非交互场景（stdin 非 TTY，或显式 `--non-interactive`）：不会弹交互选择，`--source` 缺省使用默认系统声音。
- 退出码：0 OK、2 USAGE、3 SOURCE、4 BUSY、5 DEPENDENCY、6 ENGINE、7 AUDIO。Agent 据退出码判断成败。

## 命令速查

| 命令 | 作用 |
|---|---|
| `asrgrab schema` | 输出机器可读契约（字段、命令、示例），Agent 首读 |
| `asrgrab doctor` | 环境自检（.NET / NAudio / sherpa 原生库 / ffmpeg / 音源 / 模型） |
| `asrgrab devices` | 枚举音源（OBS 式：loopback 系统声音 / input 输入设备） |
| `asrgrab start` | 开始抓取并实时转写；`--record-only` 只录音 |
| `asrgrab stop` | 停止当前会话 |
| `asrgrab status --tail N` | 查状态 + 最近 N 句 |
| `asrgrab transcribe <文件/目录>` | 离线转写（视频/音频，自动抽音轨） |
| `asrgrab grab_url <url>` | URL 进、文字出：驱动点播页播放并实时转写，收尾必发 `grab_end`（可 `--notify-cmd` 回调） |
| `asrgrab models` | 查看 / 准备本地模型（首次自动） |
| `asrgrab demo` | 生成示例转写（无需模型） |

### start 常用参数

- `--source <编号|id|名称>`：音源；支持序号、设备 id、名称子串（如 `0`、`loopback:0`、`Realtek`）。
- `--chunk <秒>`：每多少秒聚合转写一次（默认 12，调小更实时但更费算力）。
- `--model small|full|sensevoice`：small 默认（78MB，中文够用）；full 更准（约 1GB）；sensevoice 多语种。
- `--no-diarize`：关闭说话人分离（不标角色）。
- `--num-speakers <n>`：约束说话人数量，减少过碎聚类。
- `--record-only --wav <path> --max-seconds <n>`：只录不转，存 wav。
- `--detach`：后台守护模式运行，立即返回会话 id；用 `status` / `stop` 控制。

### grab_url 常用参数

- `<url>`：http(s) 点播视频页（v1 不支持直播流；iframe 内嵌播放器不保证，见「限制」）。
- `--max-seconds N`：保险丝（**全程墙钟**，从命令启动算起）；结束条件为 `ended` ∨ 切片（防自动连播串台）∨ 保险丝，先到先停。
- `--video-timeout N`：等 video 就绪超时（默认 30s）；首次需登录的站点用 `--keep-browser` 手动登录后重跑。
- `--notify-cmd "<命令>"`：收尾执行一次，环境变量 `ASRGRAB_EXIT`/`ASRGRAB_SESSION_ID`/`ASRGRAB_OUT_MD`/`ASRGRAB_OUT_JSON`；失败不影响主退出码。
- `--keep-browser`：收尾不关浏览器（自开实例也保留）。
- `--port <n>` / `--models-dir <dir>`：CDP 调试端口（默认 9222）/ 模型目录（同 `ASRGRAB_MODELS` 语义）。
- 收尾事件 `grab_end`：`end_reason: ended|clip-switch|max-seconds|error` + `exit_code`/`session_id`/`out_file`/`elapsed_s`/`segments`/`error`——分析层的标准交接点。用户主动停止（Ctrl+C / `asrgrab stop`）exit=0；会话故障 exit≠0。

### transcribe 常用参数

- 位置参数：文件或目录（目录递归转写全部媒体）。
- `--out <md|dir>`：输出目标；`--model small|full|sensevoice`（两遍转写：落稿后 `--model full` 精转对照）；`--no-diarize`、`--num-speakers`、`--models-dir`。
- 转写模式默认 **wav 常留**（`session_<id>.wav`）——为精转对照与重听锚点留本。

## 一键抓取（推荐首选）：grab_url

```
grab_url <抖音/视频链接>                  # 全程无人值守，出稿即停
grab_url <链接> --max-seconds 60          # 冒烟（截断录制）
grab_url <链接> --num-speakers 3 --label 名称 --out-dir DIR
```

内置 CDP 驱动 Edge（调试口 9222 + `--remote-allow-origins=*` + autoplay 豁免），
顺序写死：开 Edge → 打开视频 → **暂停并 seek 0**（自动播放抢跑段一律清零）→
`start --detach`（输出重定向文件，**绝不接管道**）→ 轮询 status 至 running →
**再 play** → 等 ended → stop → 出稿。产出一包（落 `--out-dir`，默认 `./transcripts`）：

- `transcript_<id>.md/.json` 实时速报稿（small）+ `session_<id>.wav` 原始录音
- `transcript_<id>_full.md` 精转稿（`transcribe --model full`；数字/日期/专名以它为准，
  与速报不一致的面值自动标 ⚠️ 速记疑误候选）
- `transcript_<id>_numbers.json/.md` 数字/日期待核清单（面值+上下文+时间戳——
  逐条判真假时「数字归属尺」的核查工单）
- `transcript_<id>_context.json` 页面语境快照（标题/主页挂链/正文摘录）
- `transcript_<id>_event.json` **完成事件**（ok/steps/files/exit_code）——无人值守交接点
- `ledgers/<账号>.jsonl` 账号级抓取台账（断言×日期锚点对齐的底账）

退出码：0 成功 / 2 参数 / 3 音源 / 6 引擎 / 7 页面视频未就绪 / 8 超时。

## Agent 手动流程（逐命令，实时抓取网页视频）

```
asrgrab doctor                              # 确认 asr_ready / capture_ready
asrgrab devices                             # 取 sources[].index（loopback 为系统声音）
asrgrab start --source <idx> --chunk 8 --detach   # 后台抓取，日志不进对话，省 token
asrgrab status --tail 20                    # 轮询进度
asrgrab stop                                # 停止并落稿
```

- 实时转写写两份：`transcript_<id>.md`（人读）、`transcript_<id>.json`（Agent 读，含 meta + segments[]）。
- 后台模式下转写事件写入 `~/.asrgrab/sessions/<id>/` 下，不在对话里刷屏；Agent 只读 `status --tail` 与最终 md。
- 切稿/裁剪：用 `tools/make_deliverable.py <json> <out.md> [--cut-ms N] [--map 旧=新] [--title 标题] [--source 文本] [--summary-file 文件] [--gap-ms N]` 生成干净出稿。**切点、姓名、标题、来源、提要全部由参数提供，脚本无任何硬编码视频内容**（`--cut-ms` 截断该毫秒之后的片段，`--map` 重命名说话人，均可重复/省略；不传则保留全部、说话人用原始聚类标签）。

### 编排纪律（实时抓取的坑）

- **不要给 `start --detach` 接管道**（`| tee` 等）：stdout 是长开的 JSONL 流，管道永不收口，会超时失败。用独立后台任务 + 输出重定向到文件，不要链式接后续命令。
- **顺序：先暂停播放 → `start` → 等采集 ready → 再播放**；否则开头会录到半截内容作废重录。
- **完成通知**：CLI 无推送机制（只有 `status` / `stop`）；「跑完→分析」交接点用后台任务等待或轮询 `status` 自己搭。
- **URL 进文字出**：本工具不下载视频、不驱动浏览器；URL 场景需外部先驱动播放（如 CDP）再交采集。能落成本地文件的，直接 `transcribe <文件>` 一条命令到底。**一键方案 = `grab_url`**（内置 CDP 驱动 + 上述全部纪律，见上节）。

## 输出示例

Markdown：

```markdown
## 转写记录

[00:00 - 00:03] 角色A：大家好，今天我们来聊……
[00:03 - 00:07] 角色B：我觉得这个方案……
```

JSON segment 字段：`start_ms`、`end_ms`、`speaker`、`text`。

## 限制（如实说明）

- 说话人编号是自动聚类（角色 A/B/C…），不自动对应真人姓名，跨片段可能变号；可用 `--num-speakers` 约束。
- 离线小模型为机器速记，专名/数字/同音字可能误识；要求高精度用 `--model full` 或事后校对。
- 逐句落盘而非逐字弹出：默认每 `--chunk` 秒转一次。
- `loopback` 抓默认输出设备的系统声音；多声卡需把目标设备设为默认或选对应 loopback 源。
- 仅 Windows 11 / .NET 8 运行；首次运行会自动准备模型（仅首次联网），之后完全离线。
- `grab_url` v1 不支持直播流（`duration=Infinity` 直接报错）；iframe 内嵌播放器只探测顶层 document，可能探不到。
