# grab_url 设计（v1）— URL 进、文字出的一条命令

- 日期：2026-10-24
- 状态：设计已与用户对齐（骨架批准 + 4 项边界补充：直播非目标、首次登录、grab_end 字段、iframe 限制）
- 背景：asrgrab「用不顺」三层复盘——缺 URL 编排层（主因）、编排纪律、一次性环境坑。目标形态：一个命令开始 → 执行完通知 → 关闭 → 分析层接手。

## 1. 目标与非目标

**目标**：`asrgrab grab_url <url>` 打开点播视频页并驱动播放，全程本地采集转写，收尾必通知，产出与 `start` 相同的 `transcript_<id>.md/.json`。

**非目标（v1 明确不做）**：

- **直播流不支持**：流水线以 `duration>0`（点播）为启动前提、`seek(0)` 为固定动作；直播页（`duration=Infinity`）直接报错退出（exit 3，错误信息注明「v1 仅支持点播」），不尝试录制、不靠 30s 超时蒙混。
- **iframe 内嵌播放器不保证支持**：CDP 层只探测顶层 document 的 `<video>`；部分 B 站等 iframe 场景可能探不到（见 §8）。
- 不下载视频、不做页面内导航/搜索找视频、不处理需复杂交互才起播的页面。

## 2. CLI 契约

```
asrgrab grab_url <url> [--max-seconds N] [--chunk N] [--model small|full|sensevoice]
                [--no-diarize] [--num-speakers n] [--video-timeout N]
                [--notify-cmd "<命令>"] [--keep-browser] [--human]
```

- `<url>`：必填 http(s) 网页地址。
- `--max-seconds N`：保险丝，到点即停（默认不设）。
- `--video-timeout N`：等待顶层 `<video>` 且 `duration>0` 的秒数（默认 30）。
- `--notify-cmd "<命令>"`：收尾执行一次（§5）。
- `--keep-browser`：收尾不关浏览器（自开实例也保留），用于首次登录或人工核对。
- 其余参数透传给采集/转写（同 `start`）。命令名 `grab_url`，同时接受别名 `grab-url`。

输出：stdout JSONL 事件流（同 `start`），末尾 `grab_end`（§4）；人类信息走 stderr；`--human` 同现有约定。

退出码（沿用现有表）：`0 OK / 2 USAGE / 3 SOURCE / 4 BUSY / 5 DEPENDENCY / 6 ENGINE / 7 AUDIO`。

## 3. 内部流水线

1. **浏览器**：探测 `127.0.0.1:9222`；已有调试实例 → 附着（收尾不关）；否则以 `launch_edge.bat` 同款参数拉起 Edge（含 `--remote-allow-origins=*`；专用 profile `~\.asrgrab\edge-profile`，登录态持久），标记「自开」。
2. **打开与等待**：新标签打开 `<url>`，轮询顶层 `<video>` 且 `duration>0`，直到 `--video-timeout`。
   - `duration=Infinity` → 立即判定直播 → exit 3。
   - 超时无 video → exit 3，stderr 提示首次登录姿势（§6）。
3. **对齐与开录**：暂停 → `seek(0)` → unmute（**不改 volume**：设 0 会令 loopback 同步静音）→ 启动采集会话（复用 Session 基建，`--chunk`/`--model` 等透传）→ **采集启动后 `Play` 开播**（先录后播，对应编排纪律「start → 等 ready → 再 play」；`play()` 被自动播放策略拒绝时告警并继续采集）。
4. **等结束**（先到先停）：`video.ended` ∨ video `src` 变化（连播切片）∨ `--max-seconds` 到点。
5. **收尾（任何路径必达）**：stop 落稿 → 若「自开」且未 `--keep-browser` 则关 Edge → 发 `grab_end` → 跑 `--notify-cmd` → 退出码。中途失败也走本收尾（已开采集收掉、自开的关掉、通知传非 0 码）。

CDP 层：按 `cdp_video.py` 的 probe / play / wait-stop 语义，用 C# 内嵌 WebSocket 客户端实现（`System.Net.WebSockets.Client` + `/json/list`），不依赖 Python。

## 4. grab_end 事件（分析层交接契约）

```json
{
  "event": "grab_end",
  "end_reason": "ended | clip-switch | max-seconds | error",
  "exit_code": 0,
  "session_id": "...",
  "out_file": "transcripts/transcript_<id>.md",
  "elapsed_s": 2135.9,
  "segments": 229,
  "error": null
}
```

- `end_reason` 封闭枚举：`ended` 自然播完 / `clip-switch` 切片即停（防连播串台）/ `max-seconds` 保险丝 / `error` 中途失败收尾。
- `error`：仅 `end_reason=error` 时输出字符串摘要；其余情况该键省略（`JsonIo` 的 WhenWritingNull 约定，示例中的 `null` 表示「无此键」）。
- `elapsed_s`：从 `grab_url` 命令启动到收尾的总秒数（含开浏览器/等 video/采集全程）。
- 此事件是「执行完通知你、开始分析」的标准交接点；等价路径是 `--notify-cmd` 回调。

## 5. --notify-cmd 约定

- 收尾时执行一次；命令失败不改主退出码，stderr 记一行警告。
- 工作目录为当前工作目录；环境变量：`ASRGRAB_EXIT` / `ASRGRAB_SESSION_ID` / `ASRGRAB_OUT_MD` / `ASRGRAB_OUT_JSON`。

## 6. 首次登录流程

专用 profile 持久化 → 登录一次长期有效。全新机器首次抓需登录的站点：

1. `asrgrab grab_url <url> --keep-browser`（超时/失败也保留浏览器）；
2. 在打开的页面手动登录（stderr 会在 video-timeout 超时、疑似需登录时提示这一步；直播判定只报不支持，登录不解决直播）；
3. 带登录态重跑 `grab_url`（无需 `--keep-browser`）。

`--video-timeout` 可放宽等待窗口（例如站点先弹登录框）。

## 7. 错误处理

- 退出码语义与现有一致（§2）：参数错 2、无 video/直播/超时 3、会话占用 4、无 Edge 或端口被非浏览器占用 5、模型/引擎 6、采集 7。
- **收尾必达**是硬性要求：任何异常路径都发 `grab_end`（`end_reason=error`）并执行 `--notify-cmd`。

## 8. 已知限制（v1）

- **直播流不支持**（明确报错，不录制）。
- **iframe 内嵌播放器可能探不到**（只探测顶层 document；部分 B 站场景）。v1 不解决，后续可扩展为 CDP 遍历 frame tree。
- 不支持「静音抓取」（不动 volume，loopback 与渲染同步）。
- 「切片即停」的停点在下一条开始瞬间，末尾可能带入极短残句（与转写 chunk 粒度一致）。
- 切片判定基于 `<video>` 的 `currentSrc` 变化：无 src/blob 的播放器（MSE/srcObject）不触发切片；广告→正片的 src 切换会被当作切片提前收尾。

## 9. 实现落点

- `Program.cs`：switch 增 `"grab_url" or "grab-url" => Commands.GrabUrl(rest)`。
- `Commands.cs`：新增 `GrabUrl`；与 `Start`/`Stop` 复用 Session 编排。
- 新增 `Cdp.cs`：`/json/list` 探测、Runtime.evaluate、ended/切片等待；语义对齐 `csharp/tools/cdp_video.py`。
- 不新增依赖（`System.Net.WebSockets.Client`）。

## 10. 测试

- 本地 `<video>` 测试页（testdata）：全流水线；双 src 轮换验证 `clip-switch`；自然播完验证 `ended`；`--max-seconds` 验证保险丝；`duration=Infinity` 页验证直播拒绝。
- 收尾必达：中途断采集 / 断 9222，确认仍发 `grab_end` + notify。
- 人工清单：抖音短片一条；自动连播确认切片即停；附着 vs 自开两种收尾行为。

## 11. 同步清单（命令契约的一部分，非可选）

落地后必须同步：`asrgrab schema`（新增 `grab_url` 与 `grab_end` 字段）、`skill/asrgrab/SKILL.md`、`skill/asrgrab/README.md`、`asrgrab-hub.zip` 重打包、GitHub 仓库文档。
