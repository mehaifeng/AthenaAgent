// 回放：把夹具里的事件（turn / think / tool / deliver）变成引擎事件，按时间喂给编排器。
// 喂多少只看回放时钟，从不看编排器演到哪里了——这就是"工具执行不等动画"在回放里的样子（规则 1）。

/** 工具事件 → 雅典娜去哪儿。类别决定公共建筑，读写按路径落到建筑；工作区外是城门。 */
export function siteForTool(e, city) {
  switch (e.category) {
    case 'terminal':
    case 'workshop':
      return 'forge';
    case 'web':
      return 'harbor';
    case 'memory':
      return 'library';
    case 'subagents':
      return 'agora';
    default:
      break;
  }
  if (e.place === 'outside') return 'gate';
  if (e.place === 'inside' && e.building) {
    if (city.buildingKeys.has(e.building)) return `b:${e.building}`;
    if (city.marketKeys.has(e.building)) return 'market';
  }
  return 'agora';
}

export function cityIndex(fixture) {
  return {
    buildingKeys: new Set((fixture.buildings ?? []).map((b) => b.key)),
    marketKeys: new Set(fixture.market ?? []),
  };
}

// 同一时刻的先后：先收尾（工具结束、交付），再开始新的（回合、等模型、工具开始）
const RANK = { 'tool-end': 0, deliver: 1, turn: 2, think: 3, 'tool-start': 4 };

/** 夹具事件 → 按时间排好的引擎事件。 */
export function toEngineEvents(fixture) {
  const city = cityIndex(fixture);
  const out = [];
  for (const e of fixture.replay?.events ?? []) {
    switch (e.type) {
      case 'turn':
        out.push({ t: e.t, type: 'turn', turn: e.turn });
        break;
      case 'think':
        out.push({ t: e.t, type: 'think', end: e.end, reasoning: e.reasoning ?? 0, estimated: !!e.estimated });
        break;
      case 'tool': {
        const site = siteForTool(e, city);
        out.push({
          t: e.t, type: 'tool-start', id: e.id, tool: e.tool, category: e.category, site,
          place: e.place, path: e.path ?? null, building: e.building ?? null, agents: e.agents ?? null,
        });
        out.push({ t: e.end ?? e.t, type: 'tool-end', id: e.id, ok: e.ok !== false });
        break;
      }
      case 'deliver':
        out.push({ t: e.t, type: 'deliver', turn: e.turn, estimated: !!e.estimated });
        break;
      default:
        break;
    }
  }
  return out
    .map((e, i) => ({ e, i }))
    .sort((a, b) => a.e.t - b.e.t || RANK[a.e.type] - RANK[b.e.type] || a.i - b.i)
    .map(({ e }) => e);
}

/** 回放时钟：feed(orchestrator, now) 把 t ≤ now 的事件按序交给编排器。 */
export function createReplay(fixture) {
  const events = toEngineEvents(fixture);
  const duration = Math.max(fixture.replay?.durationMs ?? 0, ...events.map((e) => e.t), 0);
  let next = 0;
  const fed = [];
  return {
    events,
    duration,
    get fed() { return fed; },
    feed(sink, now) {
      let n = 0;
      while (next < events.length && events[next].t <= now) {
        const e = events[next++];
        fed.push(e);
        sink.ingest(e);
        n++;
      }
      return n;
    },
    /** 当前回合号（最近一次 turn 事件），以及这一回合是否已交付。 */
    turnAt(now) {
      let turn = 0;
      let delivered = false;
      for (const e of events) {
        if (e.t > now) break;
        if (e.type === 'turn') { turn = e.turn; delivered = false; }
        if (e.type === 'deliver') delivered = true;
      }
      return { turn, delivered };
    },
  };
}
