// 旁白（设计稿 6.3）：按工具类别套模板，本地生成，零成本。做法和宠物台词库一样：一个场景一条，"|" 分隔多个变体。
// 文件用显示名（《合同》），不用路径；工具参数、推理原文、用量一概不出现。M1 搬进 locale 文件。

// 同一类动作分"出发"（go，要先走过去）和"到场"（at，人已经在那儿）两套：
// "炉火点上了"只有站在锻炉前才成立，出发时说它就是提前邀功（M0 截图审查）。
export const LINES_ZH = Object.freeze({
  turn: '交给我吧|好，我来办|这就去办',
  'read.file': '我去{place}翻翻《{name}》|去{place}找《{name}》',
  'read.file.at': '在{place}查查《{name}》|打开《{name}》看看',
  'read.dir': '我去{place}看看|去{place}里找找',
  'read.dir.at': '在{place}里找找|翻翻{place}里有什么',
  'write.file': '去{place}写《{name}》|我去誊写《{name}》',
  'write.file.at': '我在誊写《{name}》|在{place}落笔写《{name}》',
  'write.dir': '去{place}收拾一下|{place}要搭个脚手架',
  'write.dir.at': '我在{place}收拾一下|{place}搭起了脚手架',
  terminal: '去锻炉敲打一下|我去锻炉一趟',
  'terminal.at': '炉火点上了|炉火正旺',
  web: '去港口放船|我去港口一趟',
  'web.at': '船出海打听消息去了|放船出海，去外面问问',
  'memory.recall': '去图书馆翻翻旧卷|看看我记过些什么',
  'memory.write': '这件事我记下了',
  subagents: '我叫上了{count}位侍女|侍女们，分头去吧',
  workshop: '去工坊忙一下',
  outside: '我出城一趟|这一样东西在城外',
  failure: '这条路走不通，我换一条',
  deliver: '做好了，你看看|给你，刚做好的',
});

// 建筑类型 → 旁白里的叫法
export const PLACE_ZH = Object.freeze({
  stoaLibrary: '书库',
  house: '民居',
  workshop: '作坊',
  sculptureGarden: '雕塑园',
  treasury: '金库',
  warehouse: '仓库',
  agora: '广场',
  market: '市集',
});

/** 文件的显示名：取最后一段，去掉扩展名，太长就截断。 */
export function displayName(path, max = 12) {
  if (!path) return '';
  const last = path.split('/').filter(Boolean).pop() ?? '';
  const dot = last.lastIndexOf('.');
  const base = dot > 0 ? last.slice(0, dot) : last;
  return [...base].length > max ? [...base].slice(0, max - 1).join('') + '…' : base;
}

function looksLikeFile(path) {
  const last = (path ?? '').split('/').filter(Boolean).pop() ?? '';
  return last.lastIndexOf('.') > 0;
}

/** 从 "a|b|c" 里按种子挑一个；同样的种子永远挑同一个（截图可复现）。 */
export function pick(variants, seed) {
  const list = variants.split('|');
  const i = ((Math.floor(seed) % list.length) + list.length) % list.length;
  return list[i];
}

function fill(template, vars) {
  return template.replace(/\{(\w+)\}/g, (_, k) => (vars[k] ?? ''));
}

/**
 * 一次拜访（编排器里的 visit）→ 一句旁白。tools 是这次拜访包含的工具开始事件；
 * phase 是 'go'（要先走 / 瞬移过去，出发时说）或 'at'（人已经在那儿，开工时说）。
 * @returns {string|null} null 表示这一步不说话（沉思就不说话）
 */
export function lineForVisit(visit, tools, kindOf, seed = 0, lines = LINES_ZH, phase = 'go') {
  if (!visit) return null;
  if (visit.kind === 'deliver') return pick(lines.deliver, seed);
  const last = tools[tools.length - 1];
  if (!last) return null;
  const category = last.category;
  const at = (key) => (phase === 'at' && lines[`${key}.at`] ? lines[`${key}.at`] : lines[key]);
  if (category === 'terminal') return pick(at('terminal'), seed);
  if (category === 'web') return pick(at('web'), seed);
  if (category === 'workshop') return pick(lines.workshop, seed);
  if (category === 'subagents') return fill(pick(lines.subagents, seed), { count: last.agents ?? 3 });
  if (category === 'memory') return pick(last.tool === 'create_new_memory' ? lines['memory.write'] : lines['memory.recall'], seed);
  if (last.place === 'outside') return pick(lines.outside, seed);
  const place = PLACE_ZH[kindOf(last.building) ?? 'agora'] ?? '广场';
  const fileTool = tools.slice().reverse().find((t) => looksLikeFile(t.path));
  const base = category === 'write' ? 'write' : 'read';
  if (fileTool) return fill(pick(at(`${base}.file`), seed), { place, name: displayName(fileTool.path) });
  return fill(pick(at(`${base}.dir`), seed), { place });
}
