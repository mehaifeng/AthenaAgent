// 页面 → C# 的意图（封闭集合，C# 逐条校验，见 Services/GameMode/PolisIntents.cs；这里只是把形状写对）。
// 网页没有"批准"这种意图：页面显示的文字有一部分是模型写的，一旦被注入脚本，就能替你点同意（设计稿 7.1"定"）。
// 原来在 live.mjs 里；直播拿掉之后（2026-10-13）单独成一个文件。

/**
 * say / draft（2026-10-11）：城里的对话栏——你写的字托付给此刻显示的这份委托（走输入框的同一条发送路径），草稿随手同步。
 */
export const INTENT_TYPES = Object.freeze([
  'ready', 'failed', 'select-conversation', 'new-commission', 'open-file', 'attach-file', 'prefill-input',
  'stop-turn', 'accept-delivery', 'return-delivery', 'read-scroll', 'relocate', 'save-player',
  // M2：藏品的摆放 / 撤下 / 移除（只改存档），委托板、神谕、海图、居民
  'place-item', 'unplace-item', 'remove-item', 'take-suggestion', 'request-oracle', 'select-island',
  'meet-resident', 'try-resident',
  // 对话栏
  'say', 'draft',
  // 和城里的一个人说一句话（2026-10-13，原来 M-C 的问话；他的回答只回到页面——他头顶的气泡，不进任何会话）
  'chat',
  // 两位居民在街上碰上了，请他们聊两句（只用模型，不预置台词；他们的话只回到页面）
  'ambient-chat',
  // 看看一位居民那场会话（只回你和雅典娜看得见的话，进卷轴阅读器）
  'citizen-transcript',
  // M-E：讲台前的辩论（提议题、一轮的论点、不辩了）
  'debate-start', 'debate-say', 'debate-end',
]);

export function intent(type, fields = {}) {
  if (!INTENT_TYPES.includes(type)) throw new Error(`polis: '${type}' is not a page intent`);
  return { v: 1, type, ...fields };
}
