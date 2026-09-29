(function () {
  'use strict';
  window.OpenClawBridge = {
    create(options) {
      const pending = new Map();
      let requestId = 0;
      const hooks = options || {};
      const fallbackContract = window.OpenClawBridgeContract || {
        protocolVersion: 2,
        commands: {}
      };
      let contract = fallbackContract;
      const ready = fetch('bridge-contract.json', { cache: 'no-store' })
        .then(response => response.ok ? response.json() : fallbackContract)
        .then(value => { if (value && value.protocolVersion) contract = value; return contract; })
        .catch(() => contract);
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
        window.clearTimeout(operation.timer);
        if (operation.abortHandler && operation.signal) operation.signal.removeEventListener('abort', operation.abortHandler);
        if (message.ok) operation.resolve(message.data);
        else operation.reject(new Error(message.error || '操作失败。'));
      };
      if (window.chrome && window.chrome.webview) window.chrome.webview.addEventListener('message', onMessage);
      function post(command, payload, id, operationId) {
        window.chrome.webview.postMessage({
          id,
          command,
          payload: payload || {},
          protocolVersion: Number(contract.protocolVersion) || 2,
          operationId: operationId || id
        });
      }
      return {
        ready,
        call(command, payload, options) {
          const callOptions = options || {};
          return new Promise((resolve, reject) => {
            if (!window.chrome || !window.chrome.webview) {
              reject(new Error('本地安全桥接不可用，请从桌面应用启动。'));
              return;
            }
            const id = String(++requestId);
            const operationId = 'op-' + id + '-' + Date.now().toString(36);
            const spec = contract.commands && contract.commands[command] || {};
            const timeoutMs = Number(callOptions.timeoutMs || spec.timeoutMs) || (command === 'backup' ? 7200000 : 300000);
            const signal = callOptions.signal;
            const abortHandler = () => {
              if (!pending.has(id)) return;
              try { post('cancelOperation', { operationId }, 'cancel-' + id, 'cancel-' + id); } catch (_) { }
              pending.delete(id);
              window.clearTimeout(timer);
              reject(new DOMException('操作已取消。', 'AbortError'));
            };
            const timer = window.setTimeout(() => {
              if (!pending.has(id)) return;
              pending.delete(id);
              try { post('cancelOperation', { operationId }, 'cancel-timeout-' + id, 'cancel-timeout-' + id); } catch (_) { }
              reject(new Error('操作等待超时，请检查连接后重试。'));
            }, timeoutMs);
            pending.set(id, { resolve, reject, timer, signal, abortHandler, operationId });
            if (signal) {
              if (signal.aborted) { abortHandler(); return; }
              signal.addEventListener('abort', abortHandler, { once: true });
            }
            post(command, payload, id, operationId);
          });
        },
        cancelAll() {
          for (const operation of pending.values()) {
            try { post('cancelOperation', { operationId: operation.operationId }, 'cancel-all-' + Date.now(), 'cancel-all-' + Date.now()); } catch (_) { }
          }
        }
      };
    }
  };
})();
