# asrgrab — 本地视频逐字转写 + 角色分离 CLI

> 在 Windows 11 上，把**系统声音 / 麦克风 / 视频 / 音频文件**里的对话，**逐字转成文字并区分谁说了什么**，
> 输出 **Markdown + JSON**。**全程本机离线运行**，音频与文本不上传任何服务器。
> 主实现：**C# / .NET 8**（NAudio + sherpa-onnx），面向 AI Agent 设计（默认 JSON 输出）。

---

## 它解决什么问题

在抖音、B站、会议里看到一段值得收藏的对话，却只能靠手记或云端字幕——要么麻烦，要么出网不隐私。
asrgrab 让你**边看边抓**：选一个音源（类 OBS 的 loopback / 输入设备），实时把对话逐字转成带角色标签的文字稿。
不装浏览器插件、不依赖云端、中文优先、带说话人分离。

## 核心特性

- **本地隐私**：NAudio 抓声 + sherpa-onnx 离线识别，音频/文本都不出本机；模型首次运行自动准备后完全离线。
- **中文优先**：默认 `paraformer-zh-small`（78MB），中文转写质量好；另有 `full`（约 1GB）与 `sensevoice`（多语种）可选。
- **角色分离**：内置说话人聚类，自动标「角色A / 角色B…」，支持 `--num-speakers` 约束人数。
- **OBS 式音源选择**：`devices` 列出系统声音（loopback）与输入设备，按编号 / id / 名称子串指定。
- **AI Agent 契约**：默认 JSON（一次性命令单行 JSON，流式命令 JSONL），进度走 stderr 不污染 stdout；
  固定退出码；非交互不弹窗——直接给 Agent 调用。
- **双模式**：实时抓取网页正在播放的声音，或离线批量转写本地视频/音频目录。

## 安装与构建

要求：Windows 11（x64）。本作品分两部分分发：

- **技能包**（本仓库 / 技能 zip）：`SKILL.md`、说明与辅助脚本，**不含二进制**；
- **运行时包 `asrgrab-runtime-win-x64.zip`**（**Windows 11 x64 专用**，self-contained，约 75MB，解压后约 180MB）：需另行获取并解压到技能目录的 `bin\`。下载：<https://github.com/chiangchenghsin-hash/asrgrab/releases/tag/v0.1.0>

**方式一：作为 WorkBuddy 技能安装（推荐）**
1. 将技能包解压到技能目录：
   - 用户级（所有项目可用）：`%USERPROFILE%\.workbuddy\skills\asrgrab\`
   - 项目级（仅当前项目）：`<项目根>\.workbuddy\skills\asrgrab\`
2. 把 `asrgrab-runtime-win-x64.zip` 解压到该目录的 `bin\` 下，使 `bin\asrgrab.exe` 存在。
3. `bin\asrgrab.exe doctor --human` 验证。

**方式二：独立使用（命令行）**
直接用解压后的 `bin\asrgrab.exe`，无需安装到 WorkBuddy。

**首次配置（开箱即用）**

```bat
:: 进入技能目录，先自检环境
bin\asrgrab.exe doctor --human
```

> 模型在首次转写时自动准备（仅首次联网拉取约 125MB 本地模型，内置镜像回退），无需手动操作；
> 如需在首次转写前提前准备，可运行 `bin\asrgrab.exe models --download --human`。
> 转写非 wav 的视频/音频需要 `ffmpeg`（`winget install ffmpeg`）。
> 模型目录可用环境变量 `ASRGRAB_MODELS` 覆盖；状态目录 `~/.asrgrab`（`ASRGRAB_HOME` 可覆盖）。
> 若使用麦克风等输入设备音源，请在 `设置 → 隐私和安全性 → 麦克风` 中允许应用访问。

## 30 秒上手

```bat
bin\asrgrab.exe devices --human        :: 看音源列表（OBS 式）
bin\asrgrab.exe start --source 0 --chunk 8 --human   :: 抓默认系统声音实时转写
:: 在浏览器播放抖音 / B站视频……
bin\asrgrab.exe stop                   :: 停止，产出 transcripts/transcript_*.md
```

`devices --human` 大致长这样：

```
音源列表（共 3）

  idx   类型        默认  声道 名称
  ──────────────────────────────────────────────────────
  [ 0] 系统声音     .    2   扬声器 (Realtek(R) Audio)  (loopback)
  [ 1] 输入设备          1   麦克风 (USB Audio)
  [ 2] 输入设备          2   立体声混音 (Realtek)
```

- `loopback` = 系统正在播放的声音（看网页视频选这个，**不需要**立体声混音）
- `input` = 麦克风 / 线路输入 / 虚拟声卡
- `--source` 支持 **编号 / id / 名称子串**，例如 `--source 0`、`--source Realtek`

## 命令一览

| 命令 | 作用 |
|---|---|
| `asrgrab devices` | 枚举音源（OBS 式） |
| `asrgrab start` | 开始抓取 + 实时转写；`--record-only` 只录音 |
| `asrgrab stop` | 停止当前会话 |
| `asrgrab status --tail 20` | 状态 + 最近 20 句 |
| `asrgrab transcribe <文件/目录>` | 离线转写（视频/音频，自动抽音轨） |
| `asrgrab models` | 查看 / 准备本地模型 |
| `asrgrab demo` | 生成示例转写（无需模型） |
| `asrgrab doctor` | 环境自检 |
| `asrgrab schema` | 输出机器可读契约（Agent 先读这个） |

常用参数：

```bat
asrgrab start --source 0 --chunk 8 --detach          :: 后台跑，8 秒一段
asrgrab start --record-only --wav rec.wav --max-seconds 60
asrgrab start --num-speakers 2                       :: 约束 2 人，减少过碎聚类
asrgrab transcribe "D:\videos\访谈.mp4" --out D:\transcripts
asrgrab transcribe "D:\videos" --no-diarize          :: 整目录批量，不分角色
asrgrab transcribe "D:\videos" --model full          :: 用高精度大模型
```

## 给 AI Agent 的调用契约

**默认就是 JSON**，人类可读加 `--human`。

- 一次性命令（`devices`/`status`/`stop`/`doctor`/`schema`）→ **单行 JSON**
- 流式命令（`start`/`transcribe`）→ **JSONL**，一行一个事件
- 进度/人类信息走 **stderr**，不污染 stdout
- `stdin` 非 TTY 时**不会弹交互选择**，`--source` 缺省 = 默认系统声音
- 退出码：`0 OK / 2 USAGE / 3 SOURCE / 4 BUSY / 5 DEPENDENCY / 6 ENGINE / 7 AUDIO`

`start` 事件序列：

```json
{"event":"session_start","session_id":"...","pid":1234,"source":{},"out_file":"...","chunk_sec":12}
{"event":"model_loading","model":"small"}
{"event":"model_ready","model":"small"}
{"event":"tick","elapsed_s":5.0,"bytes":160000,"segments":2,"level_db":-23.4}
{"event":"segment","start_ms":0,"end_ms":3200,"speaker":"角色A","text":"大家好"}
{"event":"session_end","reason":"stopped","elapsed_s":61.2,"segments":18,"out_file":"..."}
```

后台模式返回：

```json
{"ok":true,"session_id":"...","pid":1234,"state":"running","out_file":"...","status_cmd":"asrgrab status --json","stop_cmd":"asrgrab stop --json"}
```

Agent 推荐流程：

```
asrgrab doctor
asrgrab devices
asrgrab start --source <idx> --detach
asrgrab status --tail 20
asrgrab stop
```

## 输出

每个会话/文件产出两份：

- `transcript_<id>.md` — 给人看
- `transcript_<id>.json` — 给 Agent 看（`meta` + `segments[]`）

```markdown
## 转写记录

[00:00 - 00:03] 角色A：大家好，今天我们来聊……
[00:03 - 00:07] 角色B：我觉得这个方案……
```

切稿/裁剪：`tools/make_deliverable.py <json> <out.md> [--cut-ms N] [--map 旧=新] [--title 标题] [--source 文本] [--summary-file 文件] [--gap-ms N]` 生成干净出稿。**切点、姓名、标题、来源、内容提要全部由参数/输入 meta 提供，脚本无任何硬编码视频内容**，可作通用工具分发（`--cut-ms` 截断该毫秒之后的片段，`--map` 重命名说话人，均可重复/省略；不传则保留全部内容、说话人用原始聚类标签）。

## 架构

```
音源 (loopback / input / 文件)
   │  NAudio (WASAPI) 或 ffmpeg 抽轨
   ▼
Capture 线程 ──► 帧队列 ──► 聚合(--chunk 秒) ──► 推理线程
                                                   │ sherpa-onnx (paraformer + 说话人分离)
                                                   ▼
                                    Markdown + JSON（实时追加）
```

- `csharp/Audio.cs` — WASAPI 音源枚举 / 采集、ffmpeg 解码、电平表
- `csharp/Asr.cs` — sherpa-onnx 封装（ASR + 说话人分离 + 模型自动准备 / 镜像回退）
- `csharp/Session.cs` — 会话编排、采集→聚合→推理、后台守护
- `csharp/Transcript.cs` — MD + JSON 双写
- `csharp/Commands.cs` — 命令与 Agent 契约
- `csharp/Cli.cs` — 退出码、参数解析、JSON 输出

## 实测结果（本机 Windows 11）

- 实时抓取一段约 35 分钟的圆桌 / 访谈视频：累计 229 段、约 1.27 万字落稿，角色分离可用；
- 说话人分离在 4 人官方测试音频上自动聚类正确，可用 `--num-speakers 4` 约束；
- 系统静音时 loopback 不产音频（符合预期），播放即采。

## 限制（诚实说明）

- **逐句落盘**，不是逐字弹出：默认每 `--chunk` 秒（12s）转一次，调小更实时但更费算力。
- **说话人编号是聚类**（角色 A/B），不自动对应真人姓名，跨片段编号不保证稳定。
- **首次运行自动准备模型**（约 125MB small / 1GB full），之后离线。
- `loopback` 抓的是**默认输出设备**的系统声音；多声卡需把目标设备设为默认，或选对应 `loopback` 源。
- 离线小模型为**机器速记**，专名/数字/同音字可能误识；高精度用 `--model full` 或事后人工校对。
- 仅 Windows 11 / .NET 8 运行。

## 许可证

MIT（随作品一同发布）。
