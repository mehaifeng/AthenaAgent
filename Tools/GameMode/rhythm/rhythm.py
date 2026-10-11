"""游戏模式 M0：从 Athena 的 logs.db 统计真实的工具调用节奏（只读，只输出聚合数字，不输出任何消息正文或参数）。

用法（仓库根目录）：
  python3 -I Tools/GameMode/rhythm/rhythm.py                       # 默认读开发版与 /Applications 安装版两个库
  python3 -I Tools/GameMode/rhythm/rhythm.py --db dev=<path> --names
数据库按 `mode=ro&immutable=1` 打开：应用开着时它会漏掉还没检查点的 WAL（最后几分钟），统计节奏不受影响。

Aggregate tool-call rhythm from Athena logs.db files (read-only). Prints numbers only.

Line anchors (Services/OpenAIChatService.cs, Services/Functions/FunctionRegistry.cs):
  TURN   "Starting message processing, ..." / "开始处理消息..."      one per user turn
  USAGE  "Usage <model>: ... (iteration N)" / "用量 ... (第 N 轮)"     end of one model round's stream
  NOUSE  "Usage: no usage received in iteration N"                      same, provider sent no usage
  DETECT "Detected N tool call(s)" / "检测到 N 个工具调用"              tool calls the round asked for
  EXEC   "Executing tool: <name> | args: ..." / "执行工具: ..."         a main-chat tool starts
  FSTAT  "Function <name> execution status: True|False"                 FunctionRegistry finished it
  DONE   "Tool <name> execution completed ..." / "工具 <name> 执行完成"  result appended (after its batch)
"""
import sqlite3, re, sys, statistics, collections, datetime

import os
REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
DEFAULT_DBS = {
    "dev": os.path.join(REPO, "bin", "Debug", "net10.0", "AthenaData", "Logs", "logs.db"),
    "installed": "/Applications/Athena.app/Contents/MacOS/AthenaData/Logs/logs.db",
}
BATCHING_SINCE = datetime.datetime(2026, 8, 18, tzinfo=datetime.timezone(datetime.timedelta(hours=8)))

RX = [
    ("TURN", re.compile(r'^(Starting message processing|开始处理消息)')),
    ("USAGE", re.compile(r'^(?:Usage "[^"]*": input .*\(iteration (\d+)\)|用量 "[^"]*": 输入 .*\(第 (\d+) 轮\))$', re.S)),
    ("NOUSE", re.compile(r'^Usage: no usage received in iteration (\d+)')),
    ("DETECT", re.compile(r'^(?:Detected (\d+) tool call|检测到 (\d+) 个工具调用)')),
    ("EXEC", re.compile(r'^(?:Executing tool|执行工具): "([A-Za-z0-9_\-]+)" \| ')),
    ("FSTAT", re.compile(r'^Function "([A-Za-z0-9_\-]+)" execution status: (True|False)$')),
    ("DONE", re.compile(r'^(?:Tool "([A-Za-z0-9_\-]+)" execution completed|工具 "([A-Za-z0-9_\-]+)" 执行完成)')),
    ("APPSTART", re.compile(r'^(Application starting|应用程序启动中)')),
    ("COMPACT", re.compile(r'^(Tool-loop (token budget|compression|transactional)|The provider reported a context overflow)')),
    ("APPROVAL", re.compile(r'^Approval queue received new request')),
    ("RETRY", re.compile(r'^ProviderStreamRetry')),
]

def ts(s):
    # 2026-07-23T01:59:38.6059700+08:00 -> trim the 7th fractional digit for fromisoformat
    m = re.match(r'^(.*\.\d{6})\d*(.*)$', s)
    return datetime.datetime.fromisoformat(m.group(1) + m.group(2) if m else s)

def classify(msg):
    for kind, rx in RX:
        m = rx.match(msg)
        if m:
            g = [x for x in m.groups() if x is not None]
            return kind, g
    return None, None

def load(path):
    c = sqlite3.connect(f"file:{path}?mode=ro&immutable=1", uri=True)
    rows = []
    for (i, t, msg) in c.execute("select Id, Timestamp, Message from Logs order by Id"):
        kind, g = classify(msg)
        if kind:
            rows.append((ts(t), kind, g))
    return rows

def turns_of(rows):
    """Split into turns; each turn = list of rounds; each round collects its anchors."""
    turns, cur = [], None
    for (t, kind, g) in rows:
        if kind in ("TURN", "APPSTART"):
            if cur: turns.append(cur)
            cur = {"start": t, "events": []} if kind == "TURN" else None
            continue
        if cur is not None:
            cur["events"].append((t, kind, g))
    if cur: turns.append(cur)
    return turns

def q(xs, p):
    if not xs: return float('nan')
    xs = sorted(xs)
    k = (len(xs) - 1) * p
    lo, hi = int(k), min(int(k) + 1, len(xs) - 1)
    return xs[lo] + (xs[hi] - xs[lo]) * (k - lo)

def dist(xs, unit="s", fmt="{:.2f}"):
    if not xs: return "n=0"
    return (f"n={len(xs)} p10={fmt.format(q(xs,.1))} p50={fmt.format(q(xs,.5))} p75={fmt.format(q(xs,.75))} "
            f"p90={fmt.format(q(xs,.9))} p99={fmt.format(q(xs,.99))} max={fmt.format(max(xs))} {unit}")

def analyse(rows, label):
    turns = turns_of(rows)
    out = collections.OrderedDict()
    calls_per_turn, rounds_per_turn, calls_per_round = [], [], []
    wait_first, wait_later, wait_later_clean, wait_final = [], [], [], []
    bursts, bursts_post = [], []
    exec_dur, exec_dur_noapproval = [], []
    start_gap_same_round, start_gap_cross_round = [], []
    idle_gap_cross_round = []
    burst_span = []
    anomalies = 0
    tool_names = collections.Counter()
    turn_span = []
    for turn in turns:
        ev = turn["events"]
        round_start = turn["start"]
        rounds = 0; calls = 0
        last_done = None; last_exec = None; last_exec_round = None
        cur_round = 0
        compact_since_round_start = False
        approval_pending = False
        burst = 0
        pending_exec = collections.defaultdict(list)  # name -> [start ts, had_approval]
        detected_total = 0; exec_total = 0
        turn_end = turn["start"]
        for (t, kind, g) in ev:
            turn_end = max(turn_end, t)
            if kind == "COMPACT":
                compact_since_round_start = True
            elif kind == "APPROVAL":
                for lst in pending_exec.values():
                    for item in lst: item[1] = True
            elif kind in ("USAGE", "NOUSE"):
                if burst: bursts.append(burst); (bursts_post.append(burst) if t >= BATCHING_SINCE else None); burst = 0
                rounds += 1; cur_round = rounds
                w = (t - round_start).total_seconds()
                if rounds == 1: wait_first.append(w)
                else:
                    wait_later.append(w)
                    if not compact_since_round_start: wait_later_clean.append(w)
                compact_since_round_start = False
                round_end_ts = t
            elif kind == "DETECT":
                n = int(g[0]); detected_total += n; calls_per_round.append(n)
            elif kind == "EXEC":
                name = g[0]; tool_names[name] += 1
                calls += 1; exec_total += 1
                if last_exec is not None:
                    gap = (t - last_exec).total_seconds()
                    (start_gap_same_round if last_exec_round == cur_round else start_gap_cross_round).append(gap)
                if last_done is not None and last_exec_round is not None and last_exec_round != cur_round:
                    idle_gap_cross_round.append((t - last_done).total_seconds())
                last_exec = t; last_exec_round = cur_round
                pending_exec[name].append([t, False])
                if burst == 0: burst_first = t
                burst += 1
            elif kind == "FSTAT":
                name = g[0]
                if pending_exec.get(name):
                    st, appr = pending_exec[name].pop(0)
                    d = (t - st).total_seconds()
                    exec_dur.append(d)
                    if not appr: exec_dur_noapproval.append(d)
            elif kind == "DONE":
                if burst:
                    bursts.append(burst)
                    if t >= BATCHING_SINCE: bursts_post.append(burst)
                    burst_span.append((t - burst_first).total_seconds())
                    burst = 0
                last_done = t
                round_start = t   # the next request goes out once the last result is appended
        if burst: bursts.append(burst)
        if rounds == 0: continue
        # last round with no tool calls = the final answer; its wait is the last USAGE minus the previous anchor
        calls_per_turn.append(calls); rounds_per_turn.append(rounds)
        turn_span.append((turn_end - turn["start"]).total_seconds())
        if detected_total != exec_total: anomalies += 1
    out["turns (with >=1 model round)"] = len(calls_per_turn)
    out["tool calls per turn"] = dist(calls_per_turn, "calls", "{:.0f}")
    hist = collections.Counter()
    for c in calls_per_turn:
        b = "0" if c == 0 else "1" if c == 1 else "2-3" if c <= 3 else "4-7" if c <= 7 else "8-15" if c <= 15 else "16-31" if c <= 31 else "32+"
        hist[b] += 1
    out["tool calls per turn histogram"] = " ".join(f"{k}:{hist[k]}" for k in ["0","1","2-3","4-7","8-15","16-31","32+"])
    out["model rounds per turn"] = dist(rounds_per_turn, "rounds", "{:.0f}")
    out["tool calls asked per tool round (Detected N)"] = dist(calls_per_round, "calls", "{:.0f}")
    out["model wait, first round (turn start -> stream end)"] = dist(wait_first)
    out["model wait, later rounds (last tool result -> stream end)"] = dist(wait_later)
    out["model wait, later rounds without compaction in between"] = dist(wait_later_clean)
    out["main-chat tool execution time (exec -> status)"] = dist(exec_dur)
    out["  same, excluding calls that waited on an approval prompt"] = dist(exec_dur_noapproval)
    bc = collections.Counter(bursts_post)
    out[f"parallel burst size since {BATCHING_SINCE.date()} (consecutive starts before a result)"] = (
        dist(bursts_post, "calls", "{:.0f}") + " | " + " ".join(f"{k}:{bc[k]}" for k in sorted(bc)))
    out["burst span (first start -> results appended)"] = dist(burst_span)
    out["tool start-to-start gap, same round"] = dist(start_gap_same_round)
    out["tool start-to-start gap, across rounds"] = dist(start_gap_cross_round)
    out["idle gap across rounds (last result -> next tool start)"] = dist(idle_gap_cross_round)
    out["turn span (turn start -> last anchor)"] = dist(turn_span)
    out["turns whose Detected sum != started tools (interleaving/aborts)"] = anomalies
    # share of tool calls whose start comes < X s after the previous start in the same turn (lag pressure)
    allg = start_gap_same_round + start_gap_cross_round
    for x in (0.5, 1, 3):
        out[f"tool starts within {x}s of the previous start"] = f"{sum(1 for g in allg if g < x)}/{len(allg)} ({100*sum(1 for g in allg if g < x)/max(1,len(allg)):.1f}%)"
    print(f"== {label}")
    for k, v in out.items(): print(f"  {k}: {v}")
    return tool_names

if __name__ == "__main__":
    dbs = {}
    args = sys.argv[1:]
    for i, a in enumerate(args):
        if a == "--db" and i + 1 < len(args):
            label, _, path = args[i + 1].partition("=")
            dbs[label] = path
    if not dbs:
        dbs = {k: v for k, v in DEFAULT_DBS.items() if os.path.exists(v)}
    names = collections.Counter()
    for label, path in dbs.items():
        rows = load(path)
        span = f"{rows[0][0].date().isoformat()}..{rows[-1][0].date().isoformat()}" if rows else "empty"
        names += analyse(rows, f"{label} {span}")
    if "--names" in sys.argv:
        print("== tool name counts (main chat)")
        for k, v in names.most_common(): print(f"  {k}: {v}")
