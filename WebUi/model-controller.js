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
      if (latency > 15000)
        return { label: '>15 s · 超时', className: 'latency-timeout', title: error || '超过 15 秒' };
      const measurement = String(model.latencyMeasurement || '');
      const prefix = measurement === 'first_event' || measurement === 'gateway_first_event' ? '首响应 ' : measurement === 'complete' ? '完整 ' : '';
      const measurementTitle = measurement === 'gateway_first_event'
        ? '通过 OpenClaw Gateway 测得首个流式事件，不等待完整回复'
        : measurement === 'first_event'
          ? '直接通过模型接口测得首个流式事件，不等待完整回复'
          : measurement === 'complete' ? '完整响应耗时'
            : measurement === 'cli_full' ? '完整 OpenClaw CLI 进程耗时' : '';
      if (latency <= 3000)
        return { label: prefix + latency + ' ms', className: 'latency-fast', title: measurementTitle || '3000 ms 以内' };
      if (latency <= 8000)
        return { label: prefix + latency + ' ms', className: 'latency-medium', title: measurementTitle || '3000–8000 ms' };
      return { label: prefix + latency + ' ms', className: 'latency-slow', title: measurementTitle || '超过 8000 ms' };
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

    function modelSignature(model, suffix = '') {
      return [model.id, model.name, model.provider, model.alias || '', model.status || '',
        model.latencyMs ?? '', model.lastTestedAtUtc || '', model.lastError || '',
        model.latencyMeasurement || '', suffix].join('\u001f');
    }

    function updateOrderRow(row, model, index) {
      const signature = modelSignature(model, `${index}|${state.saving}|${state.refreshing}`);
      row.dataset.modelId = model.id;
      row.draggable = !state.saving && !state.refreshing;
      if (row.dataset.modelSignature === signature) return;
      row.setAttribute('aria-label', model.id + '，' + (index === 0 ? '当前模型' : '备选模型 ' + index));
      row.querySelector('.model-order-index').textContent = String(index + 1);
      renderProviderMark(row.querySelector('.model-provider-badge'), model, true);
      row.querySelector('.model-order-main strong').textContent = safeText(model.name, model.id);
      row.querySelector('.model-order-main small').textContent = model.provider + ' · ' + model.id + (model.alias ? ' · ' + model.alias : '');
      row.querySelector('.model-order-role').textContent = index === 0 ? '当前' : '备选 ' + index;
      renderLatency(row.querySelector('.model-latency'), model);
      row.classList.toggle('model-primary-row', index === 0);
      row.dataset.virtualKey = String(model.id).toLowerCase();
      row.dataset.modelSignature = signature;
    }

    function createOrderRow(model, index, total) {
      const row = document.createElement('div');
      row.className = 'model-order-row';
      row.setAttribute('role', 'option');
      row.innerHTML = '<span class="model-drag-handle" title="拖拽调整顺序" aria-hidden="true">⋮⋮</span>' +
        '<span class="model-order-index">' + (index + 1) + '</span>' +
        '<span class="model-provider-badge" aria-hidden="true"></span>' +
        '<span class="model-order-main"><strong></strong><small></small></span>' +
        '<span class="model-order-role"></span>' +
        '<span class="model-latency"></span>';
      updateOrderRow(row, model, index);
      row.addEventListener('dragstart', event => {
        if (state.saving || state.refreshing) { event.preventDefault(); return; }
        state.draggedId = row.dataset.modelId;
        row.classList.add('dragging');
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', row.dataset.modelId);
      });
      row.addEventListener('dragover', event => {
        const targetId = row.dataset.modelId;
        if (state.saving || state.refreshing || !state.draggedId || state.draggedId === targetId) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        row.classList.add('drop-target');
      });
      row.addEventListener('dragleave', () => row.classList.remove('drop-target'));
      row.addEventListener('drop', event => {
        event.preventDefault();
        row.classList.remove('drop-target');
        const targetId = row.dataset.modelId;
        if (state.saving || state.refreshing || !state.draggedId || state.draggedId === targetId) return;
        const ids = orderIds(state.snapshot);
        const from = ids.indexOf(state.draggedId);
        const to = ids.indexOf(targetId);
        if (from < 0 || to < 0) return;
        ids.splice(from, 1);
        ids.splice(to, 0, state.draggedId);
        state.draggedId = null;
        persistOrder(ids);
      });
      row.addEventListener('dragend', () => {
        state.draggedId = null;
        row.classList.remove('dragging', 'drop-target');
        $('#modelAvailableList')?.classList.remove('drop-target');
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
      const models = selectedModels(snapshot);
      if (!models.length) {
        list.replaceChildren();
        list.className = 'model-order-list empty-state';
        list.textContent = '服务器尚未配置当前模型。';
        return;
      }
      list.className = 'model-order-list';
      const existing = new Map([...list.children]
        .filter(row => row.dataset.modelId)
        .map(row => [String(row.dataset.modelId).toLowerCase(), row]));
      const fragment = document.createDocumentFragment();
      models.forEach((model, index) => {
        const key = String(model.id).toLowerCase();
        const row = existing.get(key) || createOrderRow(model, index, models.length);
        updateOrderRow(row, model, index);
        fragment.appendChild(row);
      });
      // replaceChildren 这里只移动已有节点，未变化的模型行不会重新创建。
      list.replaceChildren(fragment);
    }

    function updateAvailableRow(row, model) {
      const signature = modelSignature(model, `${state.saving}|${state.refreshing}`);
      row.dataset.modelId = model.id;
      row.dataset.virtualKey = String(model.id).toLowerCase();
      if (row.dataset.modelSignature === signature) return;
      renderProviderMark(row.querySelector('.model-provider-badge'), model, true);
      row.querySelector('strong').textContent = safeText(model.name, model.id);
      row.querySelector('small').textContent = model.provider + ' · ' + model.id;
      renderLatency(row.querySelector('.model-latency'), model);
      row.querySelector('.model-use-button').disabled = state.saving || state.refreshing;
      row.dataset.modelSignature = signature;
    }

    function createAvailableRow(model) {
      const row = document.createElement('div');
      row.className = 'model-available-row';
      row.innerHTML = '<span class="model-provider-badge" aria-hidden="true"></span><span class="model-available-main"><strong></strong><small></small></span><span class="model-latency"></span><button class="button button-quiet model-use-button" type="button">加入备选</button>';
      row.querySelector('.model-use-button').addEventListener('click', () => {
        const id = row.dataset.modelId;
        if (id) persistOrder([...orderIds(state.snapshot), id]);
      });
      updateAvailableRow(row, model);
      return row;
    }

    function renderAvailable(snapshot) {
      const list = $('#modelAvailableList');
      if (!list.dataset.dropBound) {
        list.dataset.dropBound = 'true';
        list.setAttribute('aria-label', '可用模型；将备选模型拖到这里可移出备选队列');
        list.addEventListener('dragover', event => {
          const draggedId = state.draggedId || event.dataTransfer.getData('text/plain');
          const selected = selectedModels(state.snapshot);
          const index = selected.findIndex(model => String(model.id).toLowerCase() === String(draggedId || '').toLowerCase());
          if (state.saving || state.refreshing || index <= 0) {
            list.classList.remove('drop-target');
            return;
          }
          event.preventDefault();
          event.dataTransfer.dropEffect = 'move';
          list.classList.add('drop-target');
        });
        list.addEventListener('dragleave', event => {
          if (!event.relatedTarget || !list.contains(event.relatedTarget)) list.classList.remove('drop-target');
        });
        list.addEventListener('drop', event => {
          event.preventDefault();
          list.classList.remove('drop-target');
          if (state.saving || state.refreshing) return;
          const draggedId = state.draggedId || event.dataTransfer.getData('text/plain');
          const selected = selectedModels(state.snapshot);
          const index = selected.findIndex(model => String(model.id).toLowerCase() === String(draggedId || '').toLowerCase());
          state.draggedId = null;
          if (index <= 0) return;
          removeFallback(draggedId);
        });
      }
      const selected = new Set(selectedModels(snapshot).map(model => String(model.id || '').toLowerCase()));
      const models = (snapshot && snapshot.available || []).filter(model => {
        const id = String(model && model.id || '').toLowerCase();
        return id && !selected.has(id);
      });
      if (!models.length) {
        list.replaceChildren();
        list.className = 'model-available-list empty-state';
        list.textContent = '没有其它已发现模型；可将备选模型拖到这里移除。';
        return;
      }
      list.className = 'model-available-list';
      if (models.length > 80 && window.OpenClawVirtualList) {
        if (!list._virtualModelList) list._virtualModelList = window.OpenClawVirtualList.create(list, { rowHeight: 52, threshold: 80 });
        list._virtualModelList.setItems(models, createAvailableRow, updateAvailableRow);
        return;
      }
      if (list._virtualModelList) list._virtualModelList.clear();
      const existing = new Map([...list.children]
        .filter(row => row.dataset.modelId)
        .map(row => [String(row.dataset.modelId).toLowerCase(), row]));
      const fragment = document.createDocumentFragment();
      models.forEach(model => {
        const key = String(model.id).toLowerCase();
        const row = existing.get(key) || createAvailableRow(model);
        updateAvailableRow(row, model);
        fragment.appendChild(row);
      });
      list.replaceChildren(fragment);
    }
    function render() {
      const snapshot = state.snapshot;
      renderCurrent(snapshot);
      renderOrder(snapshot);
      renderAvailable(snapshot);
      const count = selectedModels(snapshot).length;
      $('#modelOrderCount').textContent = count ? count + ' 个已选模型' : '未配置';
      if (snapshot && snapshot.cacheSource === 'cache' && snapshot.cacheSavedAtUtc) {
        $('#modelRetrievedAt').textContent = '缓存于 ' + new Date(snapshot.cacheSavedAtUtc).toLocaleString();
      }
      else if (snapshot && snapshot.retrievedAtUtc) {
        $('#modelRetrievedAt').textContent = '更新于 ' + new Date(snapshot.retrievedAtUtc).toLocaleString();
      }
      else {
        $('#modelRetrievedAt').textContent = '尚未读取';
      }
      syncButtons();
    }

    function clearLoadPhaseTimer() {
      if (state.loadPhaseTimer) {
        window.clearTimeout(state.loadPhaseTimer);
        state.loadPhaseTimer = null;
      }
    }

    function scheduleLoadPhases() {
      clearLoadPhaseTimer();
      state.loadPhaseTimer = window.setTimeout(() => {
        if (!state.loading) return;
        state.loadPhase = 'catalog';
        syncButtons();
        state.loadPhaseTimer = window.setTimeout(() => {
          if (!state.loading) return;
          state.loadPhase = 'organize';
          syncButtons();
        }, 420);
      }, 180);
    }

    function cancelLatencyTest() {
      if (!state.testing) return;
      state.testGeneration++;
      state.testController?.abort();
      state.testController = null;
      state.testing = false;
      setStatus('模型延迟测试已取消。');
      showToast('模型延迟测试已取消。');
      syncButtons();
      render();
    }

    function syncButtons() {
      const connected = isConnected();
      const testButton = $('#testModelLatencyButton');
      const canTest = allModelIds(state.snapshot).length > 0;
      testButton.disabled = !connected || state.loading || state.refreshing || state.saving || state.adding || (!state.testing && !canTest);
      testButton.innerHTML = state.testing ? '✕ <span>取消测试</span>' : '◌ <span>测试延迟</span>';
      testButton.title = state.testing ? '取消当前模型延迟测试' : '一次性测试当前、备选和全部可用模型，最多 10 个并发';
      $('#addModelButton').disabled = !connected || state.loading || state.refreshing || state.testing || state.saving || state.adding;
      document.body.classList.toggle('model-operation-active', state.refreshing || state.testing || state.saving || state.adding);
      if (state.loading) {
        const phaseText = {
          status: '正在读取当前模型……',
          catalog: '正在读取可用模型目录……',
          organize: '正在整理模型列表……'
        };
        $('#modelLatencyStatus').textContent = phaseText[state.loadPhase] || '正在读取服务器模型配置……';
      }
      else if (state.refreshing) $('#modelLatencyStatus').textContent = '缓存已加载，正在更新模型列表……';
      else if (state.testing) $('#modelLatencyStatus').textContent = '正在并发探测模型首响应（最多 10 个同时进行）…';
      else if (state.saving) $('#modelLatencyStatus').textContent = state.pendingOrder ? '正在保存当前顺序，下一次加入已排队…' : '正在把拖拽后的顺序写入服务器…';
      else if (state.adding) $('#modelLatencyStatus').textContent = '正在写入新模型配置…';
      else if (!state.snapshot) $('#modelLatencyStatus').textContent = connected ? '点击“测试延迟”或加载模型配置。' : '连接服务器后读取模型配置。';
      else if (state.snapshot && state.snapshot.refreshError) $('#modelLatencyStatus').textContent = '模型缓存已加载，最新清单更新失败，已保留缓存。';
      else $('#modelLatencyStatus').textContent = '';
    }

    function applySnapshot(snapshot) {
      state.snapshot = snapshot;
      state.snapshotCachedAt = Date.now();
      state.cacheSource = snapshot && snapshot.cacheSource ? snapshot.cacheSource : 'remote';
      state.cacheSavedAtUtc = snapshot && snapshot.cacheSavedAtUtc ? snapshot.cacheSavedAtUtc : null;
    }

    function refreshFromServer() {
      if (!isConnected()) return Promise.resolve(null);
      if (state.refreshPromise) return state.refreshPromise;
      const controller = new AbortController();
      const generation = ++state.refreshGeneration;
      state.refreshController = controller;
      state.refreshing = true;
      syncButtons();
      render();
      let operation;
      operation = (async () => {
        try {
          const snapshot = await bridgeCall('getModels', { waitForUpdate: true }, { signal: controller.signal });
          if (generation !== state.refreshGeneration || controller.signal.aborted) return null;
          applySnapshot(snapshot);
          render();
          if (snapshot && snapshot.refreshError) {
            setStatus('模型缓存已加载，但最新清单更新失败，已保留缓存。', true);
            showToast('模型清单更新失败，已保留缓存。', true);
          }
          else setStatus('模型清单已更新。');
          return snapshot;
        } catch (error) {
          if (!controller.signal.aborted && !(error && error.name === 'AbortError')) {
            const message = error && error.message ? error.message : String(error);
            if (state.snapshot) {
              state.snapshot = { ...state.snapshot, refreshError: message };
              render();
              setStatus('模型缓存已加载，但最新清单更新失败，已保留缓存。', true);
              showToast('模型清单更新失败，已保留缓存。', true);
            }
            else reportError(error);
          }
          return null;
        } finally {
          if (state.refreshController === controller) state.refreshController = null;
          if (state.refreshPromise === operation) state.refreshPromise = null;
          if (generation === state.refreshGeneration) {
            state.refreshing = false;
            syncButtons();
            render();
          }
        }
      })();
      state.refreshPromise = operation;
      return operation;
    }

    function load() {
      if (!isConnected()) return Promise.resolve(null);
      // 连接成功后会自动读取一次；进入模型页时复用同一个请求，
      // 不要中止并立即重发，否则宿主仍在释放上一个 SSH 操作时会被判定为忙碌。
      if (state.loadPromise) return state.loadPromise;
      if (state.snapshot && !state.loading && state.snapshotCachedAt > 0 &&
          Date.now() - state.snapshotCachedAt < state.cacheTtlMs) return Promise.resolve(state.snapshot);
      const controller = new AbortController();
      const generation = ++state.loadGeneration;
      state.loadController = controller;
      state.loading = true;
      state.loadPhase = 'status';
      scheduleLoadPhases();
      syncButtons();
      let operation;
      let shouldRefresh = false;
      operation = (async () => {
        try {
          const snapshot = await bridgeCall('getModels', {}, { signal: controller.signal });
          if (generation !== state.loadGeneration || controller.signal.aborted) return null;
          applySnapshot(snapshot);
          shouldRefresh = Boolean(snapshot && snapshot.refreshPending);
          state.loadPhase = 'organize';
          render();
          // 给“整理模型列表”阶段一个可见的渲染机会，再结束读取状态。
          await new Promise(resolve => window.setTimeout(resolve, 30));
          return snapshot;
        } catch (error) {
          if (!controller.signal.aborted && !(error && error.name === 'AbortError')) reportError(error);
          return null;
        } finally {
          clearLoadPhaseTimer();
          if (state.loadController === controller) state.loadController = null;
          if (state.loadPromise === operation) state.loadPromise = null;
          if (generation === state.loadGeneration) {
            state.loading = false;
            state.loadPhase = '';
            syncButtons();
            if (shouldRefresh && isConnected()) window.setTimeout(() => refreshFromServer(), 0);
          }
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

    function applyLocalOrder(snapshot, ids, ordered, returnedModels = []) {
      const selected = new Set(ids.map(id => String(id).toLowerCase()));
      const available = new Map();
      [...(snapshot.available || []), ...returnedModels].forEach(model => {
        const id = String(model && model.id || '').toLowerCase();
        if (id && !selected.has(id) && !available.has(id)) available.set(id, model);
      });
      state.snapshot = {
        ...snapshot,
        primary: ordered[0],
        fallbacks: ordered.slice(1),
        available: [...available.values()]
      };
    }

    async function persistOrder(ids, options = {}) {
      if (!isConnected() || state.refreshing || !Array.isArray(ids) || ids.length < 1) return;
      const source = options.sourceSnapshot || state.snapshot;
      const previous = options.rollbackSnapshot || state.snapshot;
      const ordered = resolveOrder(source, ids);
      if (!ordered) { await load(); return; }
      applyLocalOrder(source, ids, ordered, options.returnedModels || []);
      state.snapshotCachedAt = Date.now();
      if (state.saving) {
        state.pendingOrder = ids.slice();
        setStatus((options.actionText || '已加入备选') + '，已排队等待当前顺序保存完成。');
        render();
        syncButtons();
        return;
      }
      state.pendingOrder = null;
      const actionText = options.actionText || '已加入备选';
      setStatus(actionText + '，正在写入服务器…');
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
        applySnapshot(result);
        render();
        setStatus(options.successText || '模型选择顺序已自动保存到服务器。');
        showToast(options.toastText || '模型顺序已保存。');
      } catch (error) {
        if (generation !== state.orderGeneration || controller.signal.aborted) return;
        state.snapshot = previous;
        render();
        reportError(error);
        showToast(options.failureText || '模型顺序保存失败，已恢复原顺序。', true);
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

    function removeFallback(modelId) {
      if (!isConnected() || state.saving || state.refreshing || !state.snapshot) return;
      const previous = state.snapshot;
      const selected = selectedModels(previous);
      const index = selected.findIndex(model => String(model.id).toLowerCase() === String(modelId || '').toLowerCase());
      if (index <= 0) return;
      const removed = selected[index];
      const ids = selected.filter((_, itemIndex) => itemIndex !== index).map(model => model.id);
      const optimistic = {
        ...previous,
        primary: selected[0],
        fallbacks: selected.slice(1).filter((_, itemIndex) => itemIndex !== index - 1),
        available: [...(previous.available || []), removed]
      };
      void persistOrder(ids, {
        sourceSnapshot: optimistic,
        rollbackSnapshot: previous,
        actionText: '已移出备选',
        successText: '备选模型已移除并保存到服务器。',
        toastText: '已移出备选模型。',
        failureText: '移除备选模型保存失败，已恢复原顺序。'
      });
    }

    async function testLatency() {
      if (!isConnected()) return;
      if (state.refreshing) return;
      if (state.testing) {
        cancelLatencyTest();
        return;
      }
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
        applySnapshot(result);
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
      if (!isConnected() || state.refreshing || state.adding) return;
      $('#modelAddForm').reset();
      $('#modelAddDialog').showModal();
      $('#modelRefInput').focus();
    }

    async function submitAdd() {
      if (!isConnected() || state.refreshing || state.adding) return;
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
        applySnapshot(result);
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
      state.refreshGeneration++;
      state.loadController?.abort(); state.refreshController?.abort(); state.testController?.abort(); state.orderController?.abort(); state.addController?.abort();
      clearLoadPhaseTimer();
      state.loadPromise = null; state.refreshPromise = null;
      state.snapshot = null; state.snapshotCachedAt = 0; state.cacheSource = ''; state.cacheSavedAtUtc = null;
      state.loading = false; state.refreshing = false; state.loadPhase = ''; state.testing = false; state.saving = false; state.adding = false;
      state.draggedId = null; state.pendingOrder = null;
      render();
    }

    return { load, render, testLatency, openAddDialog, submitAdd, reset, syncButtons };
  }

  window.OpenClawModelController = { create };
})();

