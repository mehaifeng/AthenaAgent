// 合成的积累数据（回放模式的 &demo=1）：居民、橄榄林、委托板、神谕、海图与摆好的藏品。演示、截图和冒烟测试用——
// 全部是编出来的，不来自任何人的会话。实时模式里这些都由 C# 从存档与会话推导、推来（GameModeViewModel）。
import { RESIDENTS } from './art/characters.mjs';

const RESIDENT_ZH = {
  surveyor: ['测绘员', '阿那克西曼德', '这座城的每一座房子我都量过：哪一座最大、哪一座最久没人进、里面都放着些什么，问我就对了。'],
  scribe: ['抄写员', '卡德摩斯', '我在档案馆抄写文书：报告、说明、书信、清单，雅典娜口述，我落笔。'],
  accountant: ['账房', '帕西昂', '库房里的账册都归我管：哪一笔进、哪一笔出，哪一格对不上，我一眼就看得出来。'],
  smith: ['铁匠', '赫菲斯提昂', '锻炉这边是干粗活的地方：成百上千件东西要一样样过一遍，交给我，一锤一锤地来。'],
  sailor: ['水手', '涅阿尔科斯', '船一出港，就是去外头打听消息：远方的新鲜事、别人家的价钱、要带回来的文书，我都捎得回来。'],
  librarian: ['图书管理员', '泽诺多托斯', '圆形图书馆里收着这座城的记忆：主人说过的偏好、要紧的事，记下一次，以后就不用再说。'],
  herald: ['传令官', '塔尔提比俄斯', '我站在讲台上：有人提议题，我主持辩论；到了时辰该办的差事，结果出来由我来报。'],
  painter: ['画师', '卡吕普索', '彩绘柱廊里挂的画都出自我手：配图、示意、封面，说清楚画什么、什么味道，我就画给你。'],
  artisan: ['工匠', '代达罗斯', '我在锻炉旁凿石头、做器具和机关；城外的手艺和工具要接进城里来，也是我来装。'],
  priestess: ['女祭司', '忒弥斯托克勒亚', '神庙里每天清晨有一道神谕：雅典娜替主人看一眼城里的变化，写成一卷晨报。'],
};

export function demoAccumulation(fixture) {
  const titles = ['整理合同', '写周报', '汇总预算', '照片目录', '回信', '网站文案', '会议纪要', '读书笔记', '报销单', '合同比对', '翻译', '日程'];
  const trees = Array.from({ length: 22 }, (_, i) => ({
    conversationId: `demo-${i}`,
    title: titles[i % titles.length] + (i >= titles.length ? ` ${Math.floor(i / titles.length) + 1}` : ''),
    index: i,
    stage: [3, 2, 2, 1, 1, 1, 0, 0, 2, 1, 0, 3, 1, 0, 2, 1, 0, 0, 1, 2, 0, 1][i] ?? 0,
    turns: 1 + (i % 7),
    interrupted: i === 5,
    scheduled: i === 9,
  }));
  const residents = RESIDENTS.map((kind, i) => ({
    kind,
    met: i % 3 !== 0,
    name: `${RESIDENT_ZH[kind][0]}${RESIDENT_ZH[kind][1]}`,
    trade: RESIDENT_ZH[kind][0],
    greekName: RESIDENT_ZH[kind][1],
    intro: RESIDENT_ZH[kind][2],
  }));
  const first = fixture.buildings?.[0]?.key ?? '文件';
  const suggestions = [
    { id: 'resumeCommission:整理合同', line: '上次「整理合同」停在半路，要接着做吗？' },
    { id: `sortMixedBuilding:${first}`, line: `「${first}」里混着不少东西，要不要分分类？` },
    { id: 'setOracle:', line: '在神庙设一个每日神谕' },
  ];
  const oracle = { exists: true, title: '每日神谕', body: '每日神谕已设立 · 每天 08:00 · 下一卷：明天 08:00', latestConversationId: 'demo-9' };
  const islands = [
    { id: 'sanctuary', name: '', order: 0, sanctuary: true, running: false, waiting: false, commissions: 6, current: false, missing: false },
    { id: 'w-1', name: fixture.workspaceName ?? '我的工作区', order: 1, sanctuary: false, running: true, waiting: false, commissions: 22, current: true, missing: false },
    { id: 'w-2', name: '家庭账本', order: 2, sanctuary: false, running: false, waiting: true, commissions: 4, current: false, missing: false },
    { id: 'w-3', name: '旧项目', order: 3, sanctuary: false, running: false, waiting: false, commissions: 9, current: false, missing: true },
    { id: 'w-4', name: '写作', order: 4, sanctuary: false, running: false, waiting: false, commissions: 2, current: false, missing: false },
  ];
  const at = (i) => `2026-10-0${1 + (i % 9)}T09:00:00Z`;
  const items = [
    { id: 'demo-a', kind: 'painting', title: '《海边》', state: 'accepted', placement: { venue: 'paintedStoa', slot: 0 }, createdAt: at(1) },
    { id: 'demo-b', kind: 'painting', title: '《神庙速写》', state: 'accepted', placement: { venue: 'paintedStoa', slot: 3 }, createdAt: at(2) },
    { id: 'demo-c', kind: 'scroll', title: '《周报》', state: 'accepted', placement: { venue: 'archive', slot: 0 }, createdAt: at(3) },
    { id: 'demo-d', kind: 'answer', title: '怎么报销', state: 'accepted', placement: { venue: 'archive', slot: 1 }, createdAt: at(4) },
    { id: 'demo-e', kind: 'scroll', title: '《会议纪要》', state: 'accepted', lost: true, placement: { venue: 'archive', slot: 7 }, createdAt: at(5) },
    { id: 'demo-f', kind: 'ledger', title: '《预算》', state: 'accepted', placement: { venue: 'vault', slot: 0 }, createdAt: at(6) },
    { id: 'demo-g', kind: 'ledger', title: '《流水》', state: 'accepted', createdAt: at(7) },
  ];
  return { trees, residents, suggestions, oracle, islands, items };
}

/**
 * 编出来的居民（夹具模式的 &demo=1 / &citizens=N）：形状和 C# 的 PolisAccumulation.Citizens 推来的一样（第四轮起从建筑来）——
 * 街上的最多 32 位（种子、名字、家、年纪、职业、住处），屋里的几位，人口。不来自任何人的会话。
 */
export function demoCitizens(fixture, n = 24) {
  const keys = (fixture.buildings ?? []).map((b) => b.key);
  const male = ['狄米特里', '阿里斯通', '克里同', '斐德若', '吕西斯', '卡尔米德', '美诺', '色诺芬'];
  const female = ['菲莉丝', '达芙妮', '克莱奥', '海伦娜', '伊瑞涅', '狄俄提玛', '墨利萨', '卡丽丝'];
  const ages = ['young', 'adult', 'adult', 'old'];
  const trades = ['farmer', 'herder', 'fisher', 'weaver', 'spinner', 'potter', 'baker', 'miller', 'cobbler', 'mason', 'carpenter', 'musician', 'merchant', 'idle'];
  const tradeNames = { farmer: '农夫', herder: '牧人', fisher: '渔夫', weaver: '织工', spinner: '纺纱工', potter: '陶工', baker: '面包师', miller: '磨坊工', cobbler: '鞋匠', mason: '石匠', carpenter: '木匠', musician: '乐师', merchant: '商贩', idle: '无业的人' };
  const tempers = ['爽朗', '腼腆', '较真', '爱打趣', '慢性子', '热心肠'];
  const make = (i) => {
    // 一个确定的种子：同一个 i 永远同一个人（截图可复现）；最低位交替男女
    const seed = ((Math.imul(i + 1, 2654435761) & ~1) | (i & 1)) >>> 0;
    const isFemale = (seed & 1) === 1;
    const folder = keys.length ? keys[Math.floor(i / 3) % keys.length] : null;
    const trade = trades[i % trades.length];
    return {
      id: `demo-citizen-${i}`, seed, female: isFemale, name: (isFemale ? female : male)[(seed >>> 8) % 8] + (i >= 8 ? `${i}` : ''),
      home: folder ? `b:${folder}` : 'market', age: ages[i % ages.length], trade, tradeName: tradeNames[trade], temper: tempers[i % tempers.length],
      homeName: folder ?? '市集', stall: !folder, cottage: false, nightOwl: i % 8 === 0,
    };
  };
  const count = Math.max(0, Math.min(32, Math.floor(n)));
  const street = Array.from({ length: count }, (_, i) => make(i));
  const indoors = Array.from({ length: 7 }, (_, k) => make(count + k));
  return { street, indoors, census: { total: count + indoors.length, street: count, indoors: indoors.length } };
}
