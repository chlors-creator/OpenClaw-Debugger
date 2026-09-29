(function () {
  'use strict';

  function create(deps) {
    const { $, bridgeCall, state, setStatus, showToast, reportError, setBusy, isConnected } = deps;

    function selectedModels(snapshot) {
      if (!snapshot) return [];
      return [snapshot.primary, ...(snapshot.fallbacks || [])].filter(Boolean);
    }

    function orderIds(snapshot) {
      return selectedModels(snapshot).map(model => model.id);
    }

    function allModels(snapshot) {
      if (!snapshot) return [];
      const seen = new Set();
      return [...selectedModels(snapshot), ...(snapshot.available || [])].filter(model => {
        const id = model && model.id ? String(model.id) : '';
        if (!id || seen.has(id.toLowerCase())) return false;
        seen.add(id.toLowerCase());
        return true;
      });
    }

    function allModelIds(snapshot) {
      return allModels(snapshot).map(model => model.id);
    }

    function latencyInfo(model) {
      if (!model || model.lastTestedAtUtc === null || model.lastTestedAtUtc === undefined)
        return { label: '尚未测试', className: 'latency-unset', title: '' };
      const error = model.lastError ? String(model.lastError) : '';
      if (/超时|timeout/i.test(error))
        return { label: '>15 s · 超时', className: 'latency-timeout', title: error };
      if (model.latencyMs === null || model.latencyMs === undefined)
        return { label: '测试失败', className: 'latency-failed', title: error };
      const latency = Number(model.latencyMs);
      if (!Number.isFinite(latency))
        return { label: '测试失败', className: 'latency-failed', title: error };
      if (latency <= 200)
        return { label: latency + ' ms', className: 'latency-fast', title: '200 ms 以内' };
      if (latency <= 500)
        return { label: latency + ' ms', className: 'latency-medium', title: '201–500 ms' };
      return { label: latency + ' ms', className: 'latency-slow', title: '超过 500 ms' };
    }

    function renderLatency(element, model) {
      const info = latencyInfo(model);
      element.textContent = info.label;
      element.title = info.title;
      element.classList.remove('latency-unset', 'latency-fast', 'latency-medium', 'latency-slow', 'latency-failed', 'latency-timeout');
      element.classList.add(info.className);
    }

    function isDeepSeek(model) {
      const provider = String(model && model.provider || '').toLowerCase();
      const id = String(model && model.id || '').toLowerCase();
      return provider.includes('deepseek') || id.includes('deepseek');
    }

    function renderProviderMark(element, model, compact) {
      element.replaceChildren();
      element.classList.toggle('model-provider-deepseek', isDeepSeek(model));
      if (isDeepSeek(model)) {
        const image = document.createElement('img');
        image.src = 'assets/deepseek-color.svg';
        image.alt = 'DeepSeek';
        image.loading = 'eager';
        element.appendChild(image);
        element.title = 'DeepSeek';
        return;
      }
      const provider = safeText(model && model.provider, '—');
      element.textContent = compact ? provider.slice(0, 1).toUpperCase() : provider;
      element.title = provider;
    }

    function safeText(value, fallback) {
      return value === null || value === undefined || value === '' ? fallback : String(value);
    }

    function createOrderRow(model, index, total) {
      const row = document.createElement('div');
      row.className = 'model-order-row';
      row.draggable = !state.saving;
      row.dataset.modelId = model.id;
      row.setAttribute('role', 'option');
      row.setAttribute('aria-label', model.id + '，' + (index === 0 ? '当前模型' : '备选模型 ' + index));
      row.innerHTML = '<span class="model-drag-handle" title="拖拽调整顺序" aria-hidden="true">⋮⋮</span>' +
        '<span class="model-order-index">' + (index + 1) + '</span>' +
        '<span class="model-provider-badge" aria-hidden="true"></span>' +
        '<span class="model-order-main"><strong></strong><small></small></span>' +
        '<span class="model-order-role"></span>' +
        '<span class="model-latency"></span>';
      renderProviderMark(row.querySelector('.model-provider-badge'), model, true);
      row.querySelector('.model-order-main strong').textContent = safeText(model.name, model.id);
      row.querySelector('.model-order-main small').textContent = model.provider + ' · ' + model.id + (model.alias ? ' · ' + model.alias : '');
      row.querySelector('.model-order-role').textContent = index === 0 ? '当前' : '备选 ' + index;
      renderLatency(row.querySelector('.model-latency'), model);
      if (index === 0) row.classList.add('model-primary-row');
      row.addEventListener('dragstart', event => {
        if (state.saving) { event.preventDefault(); return; }
        state.draggedId = model.id;
        row.classList.add('dragging');
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', model.id);
      });
      row.addEventListener('dragover', event => {
        if (state.saving || !state.draggedId || state.draggedId === model.id) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        row.classList.add('drop-target');
      });
      row.addEventListener('dragleave', () => row.classList.remove('drop-target'));
      row.addEventListener('drop', event => {
        event.preventDefault();
        row.classList.remove('drop-target');
        if (state.saving || !state.draggedId || state.draggedId === model.id) return;
        const ids = orderIds(state.snapshot);
        const from = ids.indexOf(state.draggedId);
        const to = ids.indexOf(model.id);
        if (from < 0 || to < 0) return;
        ids.splice(from, 1);
        ids.splice(to, 0, state.draggedId);
        state.draggedId = null;
        persistOrder(ids);
      });
      row.addEventListener('dragend', () => {
        state.draggedId = null;
        row.classList.remove('dragging', 'drop-target');
        document.querySelectorAll('.model-order-row').forEach(item => item.classList.remove('drop-target'));
      });
      return row;
    }

    function renderCurrent(snapshot) {
      const current = snapshot && snapshot.primary;
      $('#modelCurrentName').textContent = current ? safeText(current.name, current.id) : '未读取';
      $('#modelCurrentRef').textContent = current ? current.id : '连接服务器后读取模型配置';
      renderProviderMark($('#modelCurrentProvider'), current, false);
      if (current) renderLatency($('#modelCurrentLatency'), current);
      else {
        $('#modelCurrentLatency').textContent = '—';
        $('#modelCurrentLatency').title = '';
        $('#modelCurrentLatency').className = 'latency-unset';
      }
      $('#modelCurrentStatus').textContent = current ? safeText(current.status, 'unknown') : '—';
      $('#modelCurrentTested').textContent = current && current.lastTestedAtUtc ? new Date(current.lastTestedAtUtc).toLocaleString() : '尚未测试';
    }

    function renderOrder(snapshot) {
      const list = $('#modelOrderList');
      list.replaceChildren();
      const models = selectedModels(snapshot);
      if (!models.length) {
        list.className = 'model-order-list empty-state';
        list.textContent = '服务器尚未配置当前模型。';
        return;
      }
      list.className = 'model-order-list';
      models.forEach((model, index) => list.appendChild(createOrderRow(model, index, models.length)));
    }

    function renderAvailable(snapshot) {
      const list = $('#modelAvailableList');
      list.replaceChildren();
      const selected = new Set(selectedModels(snapshot).map(model => String(model.id || '').toLowerCase()));
      const models = (snapshot && snapshot.available || []).filter(model => {
        const id = String(model && model.id || '').toLowerCase();
        return id && !selected.has(id);
      });
      if (!models.length) {
        list.className = 'model-available-list empty-state';
        list.textContent = '没有其它已发现模型。';
        return;
      }
      list.className = 'model-available-list';
      models.forEach(model => {
        const row = document.createElement('div');
        row.className = 'model-available-row';
        row.innerHTML = '<span class="model-provider-badge" aria-hidden="true"></span><span class="model-available-main"><strong></strong><small></small></span><span class="model-latency"></span><button class="button button-quiet model-use-button" type="button">加入备选</button>';
        renderProviderMark(row.querySelector('.model-provider-badge'), model, true);
        row.querySelector('strong').textContent = safeText(model.name, model.id);
        row.querySelector('small').textContent = model.provider + ' · ' + model.id;
        renderLatency(row.querySelector('.model-latency'), model);
        row.querySelector('.model-use-button').addEventListener('click', () => persistOrder([...orderIds(state.snapshot), model.id]));
        list.appendChild(row);
      });
    }
    function render() {
      const snapshot = state.snapshot;
      renderCurrent(snapshot);
      renderOrder(snapshot);
      renderAvailable(snapshot);
      const count = selectedModels(snapshot).length;
      $('#modelOrderCount').textContent = count ? count + ' 个已选模型' : '未配置';
      $('#modelRetrievedAt').textContent = snapshot && snapshot.retrievedAtUtc ? '读取于 ' + new Date(snapshot.retrievedAtUtc).toLocaleString() : '尚未读取';
      syncButtons();
    }

    function syncButtons() {
      const connected = isConnected();
      $('#testModelLatencyButton').disabled = !connected || state.loading || state.testing || state.saving || !allModelIds(state.snapshot).length;
      $('#addModelButton').disabled = !connected || state.loading || state.testing || state.saving || state.adding;
      document.body.classList.toggle('model-operation-active', state.testing || state.saving || state.adding);
      if (state.loading) $('#modelLatencyStatus').textContent = '正在读取服务器模型配置…';
      else if (state.testing) $('#modelLatencyStatus').textContent = '正在逐个探测模型，可能产生少量 API 请求…';
      else if (state.saving) $('#modelLatencyStatus').textContent = state.pendingOrder ? '正在保存当前顺序，下一次加入已排队…' : '正在把拖拽后的顺序写入服务器…';
      else if (state.adding) $('#modelLatencyStatus').textContent = '正在写入新模型配置…';
      else if (!state.snapshot) $('#modelLatencyStatus').textContent = connected ? '点击“测试延迟”或加载模型配置。' : '连接服务器后读取模型配置。';
    }

    function load() {
      if (!isConnected()) return Promise.resolve(null);
      // 连接成功后会自动读取一次；进入模型页时复用同一个请求，
      // 不要中止并立即重发，否则宿主仍在释放上一个 SSH 操作时会被判定为忙碌。
      if (state.loadPromise) return state.loadPromise;
      if (state.snapshot && !state.loading) return Promise.resolve(state.snapshot);
      const controller = new AbortController();
      const generation = ++state.loadGeneration;
      state.loadController = controller;
      state.loading = true;
      syncButtons();
      let operation;
      operation = (async () => {
        try {
          const snapshot = await bridgeCall('getModels', {}, { signal: controller.signal });
          if (generation !== state.loadGeneration || controller.signal.aborted) return null;
          state.snapshot = snapshot;
          render();
          return snapshot;
        } catch (error) {
          if (!controller.signal.aborted && !(error && error.name === 'AbortError')) reportError(error);
          return null;
        } finally {
          if (state.loadController === controller) state.loadController = null;
          if (state.loadPromise === operation) state.loadPromise = null;
          if (generation === state.loadGeneration) { state.loading = false; syncButtons(); }
        }
      })();
      state.loadPromise = operation;
      return operation;
    }

    function resolveOrder(snapshot, ids) {
      if (!snapshot || !Array.isArray(ids) || !ids.length) return null;
      const byId = new Map(allModels(snapshot).map(model => [String(model.id).toLowerCase(), model]));
      const ordered = ids.map(id => byId.get(String(id).toLowerCase())).filter(Boolean);
      return ordered.length === ids.length ? ordered : null;
    }

    function applyLocalOrder(snapshot, ids, ordered) {
      const selected = new Set(ids.map(id => String(id).toLowerCase()));
      state.snapshot = {
        ...snapshot,
        primary: ordered[0],
        fallbacks: ordered.slice(1),
        available: (snapshot.available || []).filter(model => !selected.has(String(model && model.id || '').toLowerCase()))
      };
    }

    async function persistOrder(ids) {
      if (!isConnected() || !Array.isArray(ids) || ids.length < 1) return;
      const previous = state.snapshot;
      const ordered = resolveOrder(previous, ids);
      if (!ordered) { await load(); return; }
      applyLocalOrder(previous, ids, ordered);
      if (state.saving) {
        state.pendingOrder = ids.slice();
        setStatus('已加入备选，已排队等待当前顺序保存完成。');
        render();
        syncButtons();
        return;
      }
      state.pendingOrder = null;
      setStatus('已加入备选，正在写入服务器…');
      render();
      state.saving = true;
      state.orderController?.abort();
      const controller = new AbortController();
      const generation = ++state.orderGeneration;
      state.orderController = controller;
      syncButtons();
      try {
        const result = await bridgeCall('setModelOrder', { primary: ids[0], fallbacks: ids.slice(1) }, { signal: controller.signal });
        if (generation !== state.orderGeneration || controller.signal.aborted) return;
        state.snapshot = result;
        render();
        setStatus('模型选择顺序已自动保存到服务器。');
        showToast('模型顺序已保存。');
      } catch (error) {
        if (generation !== state.orderGeneration || controller.signal.aborted) return;
        state.snapshot = previous;
        render();
        reportError(error);
        showToast('模型顺序保存失败，已恢复原顺序。', true);
      } finally {
        if (state.orderController === controller) state.orderController = null;
        if (generation === state.orderGeneration) {
          const pending = state.pendingOrder;
          state.pendingOrder = null;
          state.saving = false;
          syncButtons();
          render();
          if (pending && isConnected()) void persistOrder(pending);
        }
      }
    }
    async function testLatency() {
      if (!isConnected() || state.testing) return;
      const models = allModelIds(state.snapshot);
      if (!models.length) { showToast('当前没有可测试的模型。', true); return; }
      state.testController?.abort();
      const controller = new AbortController();
      const generation = ++state.testGeneration;
      state.testController = controller;
      state.testing = true;
      syncButtons();
      try {
        const result = await bridgeCall('testModelLatency', { models }, { signal: controller.signal });
        if (generation !== state.testGeneration || controller.signal.aborted) return;
        state.snapshot = result;
        render();
        setStatus('模型延迟测试完成。');
        showToast('模型延迟测试完成。');
      } catch (error) {
        if (!controller.signal.aborted && !(error && error.name === 'AbortError')) reportError(error);
      } finally {
        if (state.testController === controller) state.testController = null;
        if (generation === state.testGeneration) { state.testing = false; syncButtons(); render(); }
      }
    }

    function openAddDialog() {
      if (!isConnected() || state.adding) return;
      $('#modelAddForm').reset();
      $('#modelAddDialog').showModal();
      $('#modelRefInput').focus();
    }

    async function submitAdd() {
      if (!isConnected() || state.adding) return;
      const payload = {
        modelRef: $('#modelRefInput').value.trim(),
        displayName: $('#modelDisplayNameInput').value.trim(),
        alias: $('#modelAliasInput').value.trim(),
        baseUrl: $('#modelBaseUrlInput').value.trim(),
        apiKey: $('#modelApiKeyInput').value
      };
      if (!payload.modelRef.includes('/')) { showToast('模型引用请填写 provider/model。', true); return; }
      state.adding = true;
      const controller = new AbortController();
      const generation = ++state.addGeneration;
      state.addController = controller;
      syncButtons();
      try {
        const result = await bridgeCall('addModel', payload, { signal: controller.signal });
        if (generation !== state.addGeneration || controller.signal.aborted) return;
        state.snapshot = result;
        $('#modelAddDialog').close('saved');
        render();
        setStatus('新模型已登记，可拖到当前模型或备选列表。');
        showToast('新模型已添加。');
      } catch (error) {
        if (!controller.signal.aborted && !(error && error.name === 'AbortError')) reportError(error);
      } finally {
        if (state.addController === controller) state.addController = null;
        if (generation === state.addGeneration) { state.adding = false; syncButtons(); render(); }
      }
    }

    function reset() {
      state.loadGeneration++;
      state.testGeneration++;
      state.orderGeneration++;
      state.addGeneration++;
      state.loadController?.abort(); state.testController?.abort(); state.orderController?.abort(); state.addController?.abort();
      state.loadPromise = null;
      state.snapshot = null; state.loading = false; state.testing = false; state.saving = false; state.adding = false;
      state.draggedId = null; state.pendingOrder = null;
      render();
    }

    return { load, render, testLatency, openAddDialog, submitAdd, reset, syncButtons };
  }

  window.OpenClawModelController = { create };
})();

