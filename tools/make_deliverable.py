"""整理 asrgrab 产出的逐段 JSON 为干净的逐字稿（Markdown + JSON）。

把会话 / 文件转写 JSON（每段含说话人聚类标签，如 角色A / 角色B / SPEAKER_01）
整理成可读的逐字稿。本脚本**不包含任何具体视频的硬编码信息**（切点、姓名、
标题、来源、内容摘要均通过参数或输入 meta 提供），可作为通用工具随技能发布。

用法：
    python make_deliverable.py <transcript.json> <out.md> [选项]

位置参数：
    transcript.json   输入：asrgrab 产出的转写 JSON（含 meta 与 segments）
    out.md            输出 Markdown 路径（同名 .json 一并写出）

选项（全部可选，无硬编码默认）：
    --cut-ms N            丢弃 start_ms >= N 的片段（例如只保留前 35:46 → --cut-ms 2135000）
    --map 旧标签=新名      重命名说话人，可重复（如 --map 角色A=张三 --map 角色B=李四）
    --title 标题           出稿标题（默认：输入 meta.title，再缺省用文件名）
    --source 文本          来源说明（默认不写；不传则不输出“来源”行）
    --summary-file 文件    内容提要，每行一条（默认不写；不传则不输出“内容提要”）
    --gap-ms N            同一说话人相邻片段间隔 <= N 毫秒则合并为一段（默认 2500）

注意：内容提要是“人工 / 外部”提供的阅读辅助，脚本本身从不自动生成摘要，
因此不会有任何与实际音频无关的硬编码文案；采集设备描述也不会被当作来源输出。
"""

import json
import os
import sys


def parse_args(argv):
    if len(argv) < 3:
        print(__doc__.strip(), file=sys.stderr)
        sys.exit(2)
    src, out_md = argv[1], argv[2]
    opts = {
        "cut_ms": None,
        "maps": {},
        "title": None,
        "source": None,
        "summary_file": None,
        "gap_ms": 2500,
    }
    i = 3
    while i < len(argv):
        a = argv[i]
        if a == "--cut-ms" and i + 1 < len(argv):
            opts["cut_ms"] = int(argv[i + 1]); i += 2
        elif a == "--map" and i + 1 < len(argv):
            old, _, new = argv[i + 1].partition("=")
            if not old or not new:
                print(f"--map 需要 旧标签=新名 形式：{argv[i + 1]!r}", file=sys.stderr)
                sys.exit(2)
            opts["maps"][old] = new; i += 2
        elif a == "--title" and i + 1 < len(argv):
            opts["title"] = argv[i + 1]; i += 2
        elif a == "--source" and i + 1 < len(argv):
            opts["source"] = argv[i + 1]; i += 2
        elif a == "--summary-file" and i + 1 < len(argv):
            opts["summary_file"] = argv[i + 1]; i += 2
        elif a == "--gap-ms" and i + 1 < len(argv):
            opts["gap_ms"] = int(argv[i + 1]); i += 2
        else:
            print(f"未知参数：{a}", file=sys.stderr)
            sys.exit(2)
    return src, out_md, opts


def fmt(ms):
    s = max(0, ms) / 1000
    return f"{int(s // 60):02d}:{int(s % 60):02d}"


def main():
    src, out_md, opts = parse_args(sys.argv)
    out_json = os.path.splitext(out_md)[0] + ".json"

    with open(src, "r", encoding="utf-8") as f:
        data = json.load(f)

    src_meta = data.get("meta", {}) or {}
    segments = data.get("segments", [])

    if opts["cut_ms"] is not None:
        segments = [s for s in segments if s.get("start_ms", 0) < opts["cut_ms"]]
    segments.sort(key=lambda s: s.get("start_ms", 0))

    def label(seg):
        raw = seg.get("speaker")
        return opts["maps"].get(raw, raw or "未标注")

    paragraphs = []
    for seg in segments:
        text = (seg.get("text") or "").strip()
        if not text:
            continue
        speaker = label(seg)
        start = int(seg.get("start_ms", 0))
        end = int(seg.get("end_ms", start))

        if (
            paragraphs
            and paragraphs[-1]["speaker"] == speaker
            and start - paragraphs[-1]["end_ms"] <= opts["gap_ms"]
        ):
            paragraphs[-1]["text"] += "，" + text
            paragraphs[-1]["end_ms"] = end
            paragraphs[-1]["segments"] += 1
        else:
            paragraphs.append(
                {
                    "start_ms": start,
                    "end_ms": end,
                    "speaker": speaker,
                    "text": text,
                    "segments": 1,
                }
            )

    for p in paragraphs:
        t = p["text"].rstrip("，")
        if t and t[-1] not in "。！？；.!?:;":
            t += "。"
        p["text"] = t

    duration_s = (segments[-1]["end_ms"] / 1000) if segments else 0
    total_chars = sum(len(p["text"]) for p in paragraphs)

    summary = []
    if opts["summary_file"]:
        with open(opts["summary_file"], "r", encoding="utf-8") as f:
            summary = [line.strip() for line in f if line.strip()]

    title = opts["title"] or src_meta.get("title") or os.path.splitext(os.path.basename(src))[0]
    captured = src_meta.get("captured_at") or src_meta.get("created_at")

    meta = dict(src_meta)
    meta.setdefault("engine", "asrgrab（本地）")
    meta.update(
        {
            "title": title,
            "covered_range": f"00:00–{fmt(duration_s * 1000)}",
            "duration_sec": round(duration_s, 1),
            "segment_count": len(segments),
            "paragraph_count": len(paragraphs),
            "char_count": total_chars,
        }
    )
    if opts["cut_ms"] is not None:
        meta["cut_ms"] = opts["cut_ms"]
    if opts["maps"]:
        meta["speaker_map"] = opts["maps"]

    os.makedirs(os.path.dirname(os.path.abspath(out_md)) or ".", exist_ok=True)
    with open(out_md, "w", encoding="utf-8") as f:
        f.write(f"# {title}\n\n")
        if opts["source"]:
            f.write(f"- 来源：{opts['source']}\n")
        if captured:
            f.write(f"- 抓取时间：{captured}\n")
        f.write(f"- 引擎：{meta['engine']}\n")
        f.write(f"- 覆盖范围：{meta['covered_range']}\n")
        f.write(f"- 规模：{meta['paragraph_count']} 段 / 约 {meta['char_count']} 字\n")
        f.write("- 说明：角色由说话人分离自动聚类，跨段可能有个别串号。\n")
        f.write("- 质量提示：机器速记，专有名词、数字、同音字可能存在错误，未人工校对。\n\n")

        if summary:
            f.write("## 内容提要\n\n")
            for item in summary:
                f.write(f"- {item}\n")
            f.write("\n---\n\n")

        f.write("## 逐字稿\n\n")
        for p in paragraphs:
            f.write(f"**[{fmt(p['start_ms'])}] {p['speaker']}**：{p['text']}\n\n")

    with open(out_json, "w", encoding="utf-8") as f:
        json.dump(
            {
                "meta": meta,
                "summary": summary,
                "paragraphs": paragraphs,
            },
            f,
            ensure_ascii=False,
            indent=2,
        )

    print(
        json.dumps(
            {
                "ok": True,
                "out_md": os.path.abspath(out_md),
                "out_json": os.path.abspath(out_json),
                "duration": fmt(duration_s * 1000),
                "paragraphs": len(paragraphs),
                "chars": total_chars,
                "segments": len(segments),
            },
            ensure_ascii=False,
        )
    )


if __name__ == "__main__":
    main()
