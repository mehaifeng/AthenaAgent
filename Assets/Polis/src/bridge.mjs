// 与宿主（应用里的 NativeWebView）之间的通道（设计稿 12.3）。
//   C# → 页面：InvokeScript("window.polis.receive(<JSON 字符串字面量>)")。C# 用 JSON 序列化器把整段 JSON 文本再编码成
//             一个字符串字面量（<、>、&、'、U+2028/2029 全部转义），页面这里再 JSON.parse——数据从不被当成脚本拼接。
//   页面 → C#：window.invokeCSharpAction(JSON)（NativeWebView 在每个平台注入的同名函数）。C# 只接受封闭集合里的意图，
//             逐条校验（PolisIntents.cs）；这里的 send 只是把形状写对。
// 没有宿主时（普通浏览器里的回放、冒烟测试），发出的意图记在 window.__polisSent 里，测试可以读。
import { intent } from './live.mjs';

export function connectHost(handlers) {
  const sent = [];
  window.__polisSent = sent;
  const hasHost = typeof window.invokeCSharpAction === 'function';

  function send(type, fields = {}) {
    const message = intent(type, fields);
    sent.push(message);
    if (sent.length > 200) sent.shift();
    if (!hasHost && typeof window.invokeCSharpAction !== 'function') return false;
    try {
      window.invokeCSharpAction(JSON.stringify(message));
      return true;
    } catch (e) {
      handlers.onError?.(e);
      return false;
    }
  }

  // 宿主推来的每条消息：{ type, ... }。认不得的类型交给 onUnknown（记一笔，不抛）。
  window.polis = {
    receive(text) {
      let message;
      try {
        message = typeof text === 'string' ? JSON.parse(text) : text;
      } catch (e) {
        handlers.onError?.(e);
        return false;
      }
      const list = Array.isArray(message) ? message : [message];
      for (const m of list) {
        const handler = m && typeof m.type === 'string' ? handlers[m.type] : null;
        try {
          if (handler) handler(m);
          else handlers.onUnknown?.(m);
        } catch (e) {
          handlers.onError?.(e);
        }
      }
      return true;
    },
  };

  return { send, get hasHost() { return typeof window.invokeCSharpAction === 'function'; } };
}
