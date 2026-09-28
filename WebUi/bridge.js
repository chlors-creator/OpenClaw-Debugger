(function () {
  'use strict';
  window.OpenClawBridge = {
    create(options) {
      const pending = new Map();
      let requestId = 0;
      const hooks = options || {};
      const onMessage = event => {
        const message = event.data || {};
        if (message.type === 'progress') {
          if (message.command === 'busy' && typeof hooks.onBusy === 'function') hooks.onBusy(Boolean(message.data && message.data.busy));
          if (message.command === 'backup' && typeof hooks.onBackup === 'function') hooks.onBackup(message.data || {});
          return;
        }
        if (!message.id || !pending.has(String(message.id))) return;
        const operation = pending.get(String(message.id));
        pending.delete(String(message.id));
        if (message.ok) operation.resolve(message.data);
        else operation.reject(new Error(message.error || '操作失败。'));
      };
      if (window.chrome && window.chrome.webview) window.chrome.webview.addEventListener('message', onMessage);
      return {
        call(command, payload) {
          return new Promise((resolve, reject) => {
            if (!window.chrome || !window.chrome.webview) {
              reject(new Error('本地安全桥接不可用，请从桌面应用启动。'));
              return;
            }
            const id = String(++requestId);
            pending.set(id, { resolve, reject });
            window.chrome.webview.postMessage({ id, command, payload: payload || {} });
            const timeoutMs = command === 'backup' ? 200 * 60 * 60 * 1000 : 300000;
            window.setTimeout(() => {
              if (!pending.has(id)) return;
              pending.delete(id);
              reject(new Error('操作等待超时，请检查连接后重试。'));
            }, timeoutMs);
          });
        }
      };
    }
  };
})();
