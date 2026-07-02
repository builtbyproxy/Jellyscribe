#!/usr/bin/env python3
"""Update the committed AI-usage ledger and the stats block in AI.md.

Claude Code writes a JSONL transcript per session under
~/.claude/projects/<sanitized-project-path>/ with per-message token usage.
Those transcripts are pruned after ~30 days, so this script folds each
session's totals into a committed ledger (docs/ai-usage.json) keyed by
session id — once a session is in the ledger, pruning can't lose it.
It then regenerates the block between the AI-USAGE markers in AI.md.

Runs automatically at the end of every Claude Code session in this repo
(SessionEnd hook in .claude/settings.json). Safe to run by hand:

    python3 scripts/update-ai-usage.py
"""

import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
LEDGER = REPO_ROOT / "docs" / "ai-usage.json"
AI_MD = REPO_ROOT / "AI.md"
START = "<!-- AI-USAGE:START -->"
END = "<!-- AI-USAGE:END -->"

# Claude Code names the transcript dir after the project path with
# non-alphanumeric characters replaced by '-'. AI_USAGE_TRANSCRIPTS
# overrides it (needed when running from a git worktree, whose path
# maps to a different transcript dir than the main checkout).
import os

sanitized = re.sub(r"[^A-Za-z0-9]", "-", str(REPO_ROOT))
TRANSCRIPTS = Path(
    os.environ.get(
        "AI_USAGE_TRANSCRIPTS", Path.home() / ".claude" / "projects" / sanitized
    )
)

USAGE_KEYS = {
    "input": "input_tokens",
    "output": "output_tokens",
    "cache_write": "cache_creation_input_tokens",
    "cache_read": "cache_read_input_tokens",
}


def session_totals(path):
    models, first_ts, last_ts = {}, None, None
    for line in path.open(encoding="utf-8", errors="replace"):
        try:
            d = json.loads(line)
        except json.JSONDecodeError:
            continue
        ts = d.get("timestamp")
        if ts:
            first_ts = min(first_ts or ts, ts)
            last_ts = max(last_ts or ts, ts)
        msg = d.get("message") or {}
        usage, model = msg.get("usage"), msg.get("model")
        if usage and model and model != "<synthetic>":
            m = models.setdefault(model, {k: 0 for k in USAGE_KEYS})
            for ours, theirs in USAGE_KEYS.items():
                m[ours] += usage.get(theirs, 0) or 0
    if not models:
        return None
    return {"first_ts": first_ts, "last_ts": last_ts, "models": models}


def main():
    ledger = {"tracked_since": "2026-06-29", "sessions": {}}
    if LEDGER.exists():
        ledger = json.loads(LEDGER.read_text())

    merged = 0
    if TRANSCRIPTS.is_dir():
        for f in TRANSCRIPTS.glob("*.jsonl"):
            totals = session_totals(f)
            if totals:
                ledger["sessions"][f.stem] = totals  # newest data wins
                merged += 1

    LEDGER.parent.mkdir(parents=True, exist_ok=True)
    LEDGER.write_text(json.dumps(ledger, indent=2, sort_keys=True) + "\n")

    # Aggregate for the AI.md block
    by_model, last_ts = {}, None
    for s in ledger["sessions"].values():
        if s.get("last_ts"):
            last_ts = max(last_ts or s["last_ts"], s["last_ts"])
        for model, u in s["models"].items():
            agg = by_model.setdefault(model, {k: 0 for k in USAGE_KEYS})
            for k in USAGE_KEYS:
                agg[k] += u.get(k, 0)

    def fmt(n):
        return f"{n:,}"

    rows = "\n".join(
        f"| `{m}` | {fmt(u['input'])} | {fmt(u['output'])} | {fmt(u['cache_write'])} | {fmt(u['cache_read'])} |"
        for m, u in sorted(by_model.items())
    )
    grand = {k: sum(u[k] for u in by_model.values()) for k in USAGE_KEYS}
    updated = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    block = f"""{START}
_Tracked since **{ledger["tracked_since"]}** across **{len(ledger["sessions"])}** recorded session(s); last updated **{updated}** (UTC). Counts are a floor — see the caveats below the table._

| Model | Input | Output | Cache write | Cache read |
|---|---:|---:|---:|---:|
{rows}
| **Total** | **{fmt(grand['input'])}** | **{fmt(grand['output'])}** | **{fmt(grand['cache_write'])}** | **{fmt(grand['cache_read'])}** |
{END}"""

    if AI_MD.exists():
        text = AI_MD.read_text()
        pattern = re.compile(re.escape(START) + r".*?" + re.escape(END), re.S)
        if pattern.search(text):
            AI_MD.write_text(pattern.sub(lambda _: block, text))
        else:
            print(f"warning: AI-USAGE markers not found in {AI_MD}", file=sys.stderr)

    print(f"merged {merged} session(s); ledger has {len(ledger['sessions'])}")


if __name__ == "__main__":
    main()
