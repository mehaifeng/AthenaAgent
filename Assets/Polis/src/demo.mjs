// 合成的积累数据（回放模式的 &demo=1）：居民、橄榄林、委托板、神谕、海图与摆好的藏品。演示、截图和冒烟测试用——
// 全部是编出来的，不来自任何人的会话。实时模式里这些都由 C# 从存档与会话推导、推来（GameModeViewModel）。
import { RESIDENTS } from './art/characters.mjs';

const RESIDENT_ZH = {
  surveyor: ['测绘员', '我替雅典娜量过这座城：哪个文件夹最大、哪里的东西最旧，她都能告诉你。'],
  scribe: ['抄写员', '报告、说明、清单，她都能替你起草，也能改你写了一半的那份。'],
  accountant: ['账房', '一堆表格要汇总、对账、出图？交给她，算错的那一格也会指出来。'],
  smith: ['铁匠', '要跑命令、装工具、批量改名，她在锻炉前就能办——危险的那几步会先问你。'],
  sailor: ['水手', '船出海就是去网上打听：查资料、下载文件、替你点完一整套网页。'],
  librarian: ['图书管理员', '你常用的格式、你的偏好，告诉她一次，她就记在图书馆里。'],
  maiden: ['黄金侍女', '活多的时候，雅典娜会叫上我们几个分头去办，办完一起回来。'],
  herald: ['传令官', '要定时做的事（每天早上、每周一），交给雅典娜排成定时任务，结果由我来报。'],
  painter: ['画师', '要一张配图、一幅示意？说清楚画什么，她就画给你。'],
  artisan: ['工匠', '雅典娜还能接上别的工具和技能——要接什么，问她就行。'],
  priestess: ['女祭司', '神庙里的每日神谕：每天早上，她替你看一眼这座城的变化。'],
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
    name: RESIDENT_ZH[kind][0],
    intro: RESIDENT_ZH[kind][1],
    tryLabel: '请雅典娜试试',
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
