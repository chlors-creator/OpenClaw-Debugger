(function () {
  'use strict';
  const $ = (selector, root) => (root || document).querySelector(selector);
  const $$ = (selector, root) => Array.from((root || document).querySelectorAll(selector));
  const appState = window.OpenClawAppState.create();
  function bridgeCall(command, payload, options) {
    if (!appState.bridge) return Promise.reject(new Error('本地安全桥接尚未初始化。'));
    return appState.bridge.call(command, payload, options);
  }

  function syncRenameButton() {
    const connected = $('.connection-chip').classList.contains('connected');
    const row = appState.stickerRows.find(item => item.imagePath === appState.currentSticker);
    $('#renameStickerButton').disabled = !connected || !row || !appState.stickerEditingEnabled ||
      appState.stickerPreviewLoading || appState.uploadInProgress || appState.renameInProgress || appState.dirtyMemory || appState.dirtyStickers || appState.dirtyRaw;
  }

  function syncBackupButton() {
    const button = $('#backupButton');
    const cancelButton = $('#cancelBackupButton');
    const actions = $('.top-actions');
    const connected = $('.connection-chip').classList.contains('connected');
    actions.classList.toggle('backup-controls-active', appState.backupActive && !appState.backupControlsClosing);
    cancelButton.hidden = false;
    cancelButton.setAttribute('aria-hidden', String(!appState.backupActive || appState.backupControlsClosing));
    if (appState.backupActive) {
      button.disabled = appState.backupCancelRequested;
      button.innerHTML = appState.backupCancelRequested
        ? '… <span>正在取消</span>'
        : (appState.backupPaused ? '▶ <span>继续备份</span>' : 'Ⅱ <span>暂停备份</span>');
      cancelButton.disabled = appState.backupCancelRequested;
      cancelButton.innerHTML = appState.backupCancelRequested ? '… <span>正在取消</span>' : '✕ <span>取消备份</span>';
      return;
    }
    button.innerHTML = '▣ <span>备份服务器</span>';
    button.disabled = !connected;
    cancelButton.disabled = true;
    cancelButton.innerHTML = '✕ <span>取消备份</span>';
  }

  function syncModelButtons() {
    appState.modelController?.syncButtons();
  }

  function isConnected() {
    return $('.connection-chip').classList.contains('connected');
  }

  function syncConnectionButton() {
    const button = $('#connectButton');
    if (!button) return;
    const connected = isConnected();
    const pending = appState.connecting || appState.connection.disconnecting;
    const busy = Boolean(appState.connection.busy || appState.uploadInProgress || appState.renameInProgress);
    const hoverDisconnect = connected && !pending && (button.matches(':hover') || button.matches(':focus-visible'));
    const icon = appState.connection.disconnecting ? '…' : appState.connecting ? '◌' : connected ? '✓' : '⟷';
    const label = appState.connection.disconnecting ? '正在断开' : appState.connecting ? '取消连接' : connected ? (hoverDisconnect ? '断开连接' : '已连接') : '连接服务器';
    const signature = icon + '|' + label;
    button.classList.toggle('connection-button-connected', connected);
    button.classList.toggle('connection-button-pending', pending);
    // 连接过程可以主动取消，避免网络波动时整个顶栏看起来像“没有反应”。
    button.disabled = appState.connection.disconnecting || (busy && !appState.connecting);
    button.setAttribute('aria-label', label);
    button.title = connected && !pending ? '已连接；悬停后可断开连接' : label;
    if (button.dataset.connectionSignature !== signature) {
      button.innerHTML = icon + ' <span class="connection-button-label">' + label + '</span>';
      button.dataset.connectionSignature = signature;
    }
  }

  function setBusy(busy) {
    const busyNow = Boolean(
      busy ||
      appState.connection.disconnecting ||
      appState.uploadInProgress ||
      appState.renameInProgress
    );
    appState.connection.busy = busyNow;
    const connected = $('.connection-chip').classList.contains('connected');
    document.body.classList.toggle('busy', busyNow);
    $('#refreshButton').disabled = busyNow || !connected;
    if (appState.backupActive) {
      $('#backupButton').disabled = appState.backupCancelRequested;
    } else {
      $('#backupButton').disabled = busyNow || !connected;
    }
    $('#settingsConnectButton').disabled = busyNow;
    $('#saveSettingsButton').disabled = busyNow;
    $('#exportLogsButton').disabled = busyNow;
    $('#saveMemoryButton').disabled = busyNow || !appState.memoryEditing || !appState.dirtyMemory;
    $('#saveStickerButton').disabled = busyNow || !appState.stickerEditingEnabled || !appState.dirtyStickers;
    const uploadActive = appState.uploadInProgress;
    const uploadReady = connected && (!busyNow || uploadActive);
    $('#pickStickerButton').disabled = !uploadReady || uploadActive;
    $('#uploadDropzone').classList.toggle('disabled', !uploadReady);
    syncRenameButton();
    syncModelButtons();
    syncConnectionButton();
  }

  function setStatus(text, isError) {
    $('#statusText').textContent = text;
    $('#statusText').classList.toggle('error-text', Boolean(isError));
  }

  function showToast(text, isError) {
    const el = $('#toast');
    el.textContent = text;
    el.classList.toggle('error', Boolean(isError));
    el.classList.add('show');
    window.clearTimeout(appState.toastTimer);
    appState.toastTimer = window.setTimeout(() => el.classList.remove('show'), 3400);
  }

  function reportError(error) {
    const message = error && error.message ? error.message : String(error);
    setStatus(message, true);
    showToast(message, true);
  }

  function resetDisconnectedUi() {
    appState.memory.readController?.abort();
    appState.memory.saveController?.abort();
    appState.sticker.previewController?.abort();
    appState.sticker.saveController?.abort();
    appState.sticker.renameController?.abort();
    appState.sticker.rawSaveController?.abort();
    appState.memoryFiles = [];
    appState.currentMemory = null;
    appState.memoryOriginal = '';
    appState.memoryEditing = false;
    appState.dirtyMemory = false;
    appState.stickerRows = [];
    appState.originalStickerRows = [];
    appState.currentSticker = null;
    appState.originalCatalog = '';
    appState.originalManifest = '';
    appState.stickerEditingEnabled = false;
    appState.dirtyStickers = false;
    appState.dirtyRaw = false;
    appState.modelController?.reset();
    $('.connection-chip').classList.remove('connected');
    $('#connectionStatus').textContent = '尚未连接';
    $('#memoryCount').textContent = '—';
    $('#stickerCount').textContent = '—';
    $('#memoryPageCount').textContent = '0';
    $('#stickerListCount').textContent = '0';
    $('#memoryTitle').textContent = '选择左侧文件';
    $('#memoryInfo').textContent = '内容会在读取后显示。';
    $('#memoryEditor').value = '';
    $('#memoryEditor').readOnly = true;
    $('#memoryEditorState').textContent = '只读预览';
    $('#cancelMemoryButton').disabled = true;
    $('#rawStickerButton').disabled = true;
    $('#saveStickerButton').disabled = true;
    $('#stickerTitle').textContent = '选择一张贴图';
    $('#stickerFormat').textContent = 'IMAGE';
    $('#selectedTags').textContent = '选择图片后显示标签';
    $('#stickerSize').textContent = '';
    $('#stickerStatus').textContent = '连接后读取目录和标签格式。';
    $('#stickerImage').hidden = true;
    $('#stickerImage').removeAttribute('src');
    $('.placeholder-art').hidden = false;
    $('#rootPathsText').textContent = '连接后显示工作区和表情包目录';
    $('#refreshButton').disabled = true;
    $('#backupButton').disabled = true;
    appState.memoryController?.renderList();
    appState.stickerController?.renderList();
    setDirtyState();
    syncConnectionButton();
  }

  function sizeLabel(size) {
    const value = Math.max(0, Number(size) || 0);
    if (value < 1024) return value.toFixed(value < 10 && value % 1 ? 1 : 0) + ' B';
    if (value < 1048576) return (value / 1024).toFixed(1) + ' KiB';
    if (value < 1073741824) return (value / 1048576).toFixed(1) + ' MiB';
    if (value < 1099511627776) return (value / 1073741824).toFixed(2) + ' GiB';
    return (value / 1099511627776).toFixed(2) + ' TiB';
  }

  function etaLabel(seconds) {
    if (seconds === null || seconds === undefined || !Number.isFinite(Number(seconds))) return '计算中';
    const total = Math.max(0, Math.ceil(Number(seconds)));
    if (total < 60) return '约 ' + total + ' 秒';
    const minutes = Math.floor(total / 60);
    const remainingSeconds = total % 60;
    if (minutes < 60) return '约 ' + minutes + ' 分 ' + remainingSeconds + ' 秒';
    const hours = Math.floor(minutes / 60);
    const remainingMinutes = minutes % 60;
    return '约 ' + hours + ' 小时 ' + remainingMinutes + ' 分';
  }

  function switchTab(name) {
    appState.currentTab = name;
    $$('.nav-tab').forEach(button => button.classList.toggle('active', button.dataset.tab === name));
    $$('.page').forEach(page => page.classList.toggle('active', page.id === 'page-' + name));
    const activeButton = $('.nav-tab.active');
    const nav = $('.main-nav');
    if (activeButton) nav.style.setProperty('--active-x', activeButton.offsetLeft + 'px');
    if (name === 'models' && $('.connection-chip').classList.contains('connected') &&
        !appState.model.snapshot && !appState.model.loading) appState.modelController?.load();
  }

  function setDirtyState() {
    const dirty = appState.dirtyMemory || appState.dirtyStickers || appState.dirtyRaw;
    window.clearTimeout(appState.dirtySyncTimer);
    appState.dirtySyncTimer = window.setTimeout(() => bridgeCall('draftState', { dirty: dirty }).catch(() => {}), 90);
    $('#saveMemoryButton').disabled = !appState.memoryEditing || !appState.dirtyMemory;
    $('#saveStickerButton').disabled = !appState.stickerEditingEnabled || !appState.dirtyStickers;
    $('#memoryEditorState').textContent = appState.dirtyMemory ? '有未保存修改' : (appState.memoryEditing ? '编辑模式' : '只读预览');
    syncRenameButton();
  }

  function applyTheme(theme) { return appState.themeController && appState.themeController.applyTheme(theme); }
  function persistTheme(theme) { return appState.themeController && appState.themeController.persistTheme(theme); }
  function initializeBackdrop() { return appState.themeController && appState.themeController.initializeBackdrop(); }
  function applyBackdrop() { return appState.themeController && appState.themeController.applyBackdrop(); }
  function resetThemePalette() { return appState.themeController && appState.themeController.resetPalette(); }
  function setBackdropValue(kind, value) { return appState.themeController && appState.themeController.setBackdropValue(kind, value); }
  function resetBackdrop() { return appState.themeController && appState.themeController.resetBackdrop(); }

  function renderMemoryList() { return appState.memoryController && appState.memoryController.renderList(); }
  function selectMemory(file) { return appState.memoryController && appState.memoryController.select(file); }
  function saveMemory() { return appState.memoryController && appState.memoryController.save(); }

  function renderStickerList() { return appState.stickerController && appState.stickerController.renderList(); }
  function selectSticker(row) { return appState.stickerController && appState.stickerController.select(row); }
  function saveStickerRows() { return appState.stickerController && appState.stickerController.save(); }
  function openRenameDialog() { return appState.stickerController && appState.stickerController.openRename(); }
  function submitStickerRename() { return appState.stickerController && appState.stickerController.submitRename(); }
  function rowsPayload() { return appState.stickerController ? appState.stickerController.rowsPayload() : []; }
  function snapshotStickerRows() { return appState.stickerController ? appState.stickerController.snapshotRows() : []; }
  function updateStickerDirty() { return appState.stickerController && appState.stickerController.updateDirty(); }
  function fileIsGif(path) { return appState.stickerController ? appState.stickerController.fileIsGif(path) : /\.gif$/i.test(path); }

  function encodeBase64(buffer) { return appState.uploadController && appState.uploadController.encodeBase64(buffer); }
  function hasDraggedFiles(dataTransfer) { return appState.uploadController ? appState.uploadController.hasDraggedFiles(dataTransfer) : false; }
  function getDroppedFiles(dataTransfer) { return appState.uploadController ? appState.uploadController.getDroppedFiles(dataTransfer) : []; }
  function uploadFiles(fileList) { return appState.uploadController && appState.uploadController.uploadFiles(fileList); }
  function cancelUpload() { return appState.uploadController && appState.uploadController.cancelUpload(); }

  function loadModels() { return appState.modelController && appState.modelController.load(); }
  function testModelLatency() { return appState.modelController && appState.modelController.testLatency(); }
  function openAddModelDialog() { return appState.modelController && appState.modelController.openAddDialog(); }
  function submitAddModel() { return appState.modelController && appState.modelController.submitAdd(); }

  function renderBackupProgress(data) { return appState.backupController && appState.backupController.renderProgress(data); }
  function backupServer() { return appState.backupController && appState.backupController.backup(); }
  function toggleBackupPause() { return appState.backupController && appState.backupController.togglePause(); }
  function cancelBackup() { return appState.backupController && appState.backupController.cancel(); }

  function getConnectionSettings() { return appState.settingsController && appState.settingsController.getConnectionSettings(); }
  function fillSettings(value, retentionOverride) { return appState.settingsController && appState.settingsController.fillSettings(value, retentionOverride); }
  function saveConnectionSettings(andConnect) { return appState.settingsController && appState.settingsController.saveConnectionSettings(andConnect); }

  function clearStickerImageCache() { return appState.stickerCacheController && appState.stickerCacheController.clearStickerImageCache(); }
  function moveStickerImageCache(oldPath, newPath) { return appState.stickerCacheController && appState.stickerCacheController.moveStickerImageCache(oldPath, newPath); }
  function loadStickerImage(path, options) { return appState.stickerCacheController ? appState.stickerCacheController.loadStickerImage(path, options) : Promise.reject(new Error('图片缓存尚未初始化。')); }
  function loadStickerThumbnailData(path) { return appState.stickerCacheController ? appState.stickerCacheController.loadStickerThumbnailData(path) : Promise.reject(new Error('缩略图缓存尚未初始化。')); }
  function loadStickerThumbnail(row, image) { return appState.stickerCacheController ? appState.stickerCacheController.loadStickerThumbnail(row, image) : Promise.resolve(); }

  function review(title, before, after) {
    $('#reviewTitle').textContent = title;
    $('#reviewBefore').textContent = before.length > 90000 ? before.slice(0, 90000) + '\n…（内容过长，已截断预览）' : before;
    $('#reviewAfter').textContent = after.length > 90000 ? after.slice(0, 90000) + '\n…（内容过长，已截断预览）' : after;
    const dialog = $('#reviewDialog');
    dialog.showModal();
    return new Promise(resolve => dialog.addEventListener('close', () => resolve(dialog.returnValue === 'confirm'), { once: true }));
  }

  async function connectServer() {
    if (appState.connecting || appState.connection.disconnecting) return;
    if (appState.dirtyMemory || appState.dirtyStickers || appState.dirtyRaw) {
      showToast('请先保存或放弃未完成的修改，再重新扫描。', true);
      return;
    }
    if (appState.backupActive || appState.uploadInProgress || appState.renameInProgress) {
      showToast('当前有远程操作正在进行，请完成或取消后再连接。', true);
      return;
    }
    const generation = ++appState.connection.connectGeneration;
    const controller = new AbortController();
    appState.connection.connectController = controller;
    appState.connecting = true;
    syncConnectionButton();
    $('#connectionStatus').textContent = '正在连接';
    $('.connection-chip').classList.remove('connected');
    setStatus('正在连接服务器并扫描允许管理的目录…');
    setBusy(true);
    try {
      await bridgeCall('saveSettings', getConnectionSettings(), { signal: controller.signal });
      const result = await bridgeCall('connect', {}, { signal: controller.signal });
      if (generation !== appState.connection.connectGeneration || controller.signal.aborted) return;
      clearStickerImageCache();
      appState.memoryFiles = result.memoryFiles || [];
      appState.stickerRows = result.stickerFiles || [];
      appState.modelController?.reset();
      appState.originalStickerRows = snapshotStickerRows();
      appState.stickerEditingEnabled = Boolean(result.stickerEditingEnabled);
      appState.originalCatalog = result.catalogText || '';
      appState.originalManifest = result.manifestText || '';
      appState.dirtyMemory = false; appState.dirtyStickers = false; appState.dirtyRaw = false; appState.currentMemory = null; appState.currentSticker = null;
      $('#connectionStatus').textContent = result.connectionStatus;
      $('.connection-chip').classList.add('connected');
      syncConnectionButton();
      $('#memoryCount').textContent = result.memoryCount;
      $('#stickerCount').textContent = result.stickerCount;
      $('#memoryPageCount').textContent = result.memoryCount;
      $('#stickerListCount').textContent = result.stickerCount;
      $('#rootPathsText').textContent = '工作区：' + result.workspacePath + ' · 表情包：' + result.stickersPath;
      $('#stickerStatus').textContent = result.stickerStatus;
      $('#rawStickerButton').disabled = !appState.stickerEditingEnabled;
      $('#saveStickerButton').disabled = true;
      $('#refreshButton').disabled = false; $('#backupButton').disabled = false;
      setBusy(false);
      renderMemoryList(); renderStickerList(); setDirtyState();
      loadModels();
      setStatus('SSH 连接成功，已读取 ' + result.memoryCount + ' 个记忆文档和 ' + result.stickerCount + ' 张图片。');
      showToast('服务器连接成功。');
    } catch (error) {
      if (controller.signal.aborted || (error && error.name === 'AbortError')) return;
      $('#connectionStatus').textContent = '连接失败';
      $('.connection-chip').classList.remove('connected');
      appState.modelController?.reset();
      $('#backupButton').disabled = true;
      syncConnectionButton();
      reportError(error);
    } finally {
      if (generation === appState.connection.connectGeneration) {
        appState.connecting = false;
        if (appState.connection.connectController === controller) appState.connection.connectController = null;
        setBusy(false);
        syncConnectionButton();
      }
    }
  }

  async function disconnectServer() {
    if (appState.connection.disconnecting || !isConnected()) return;
    if (appState.dirtyMemory || appState.dirtyStickers || appState.dirtyRaw) {
      showToast('请先保存或放弃未完成的修改，再断开服务器。', true);
      return;
    }
    if (appState.backupActive || appState.uploadInProgress || appState.renameInProgress) {
      showToast('当前有远程操作正在进行，请完成或取消后再断开。', true);
      return;
    }
    appState.connection.connectGeneration++;
    appState.connection.connectController?.abort();
    const generation = ++appState.connection.disconnectGeneration;
    const controller = new AbortController();
    appState.connection.disconnectController = controller;
    appState.connection.disconnecting = true;
    $('#connectionStatus').textContent = '正在断开';
    setStatus('正在关闭 SSH 复用连接…');
    syncConnectionButton();
    setBusy(true);
    try {
      await bridgeCall('disconnect', {}, { signal: controller.signal });
      if (generation !== appState.connection.disconnectGeneration || controller.signal.aborted) return;
      resetDisconnectedUi();
      setStatus('服务器连接已断开。');
      showToast('已断开服务器连接。');
    } catch (error) {
      if (!controller.signal.aborted && !(error && error.name === 'AbortError')) reportError(error);
    } finally {
      if (generation === appState.connection.disconnectGeneration) {
        appState.connection.disconnecting = false;
        if (appState.connection.disconnectController === controller) appState.connection.disconnectController = null;
        setBusy(false);
        syncConnectionButton();
      }
    }
  }

  function cancelConnect() {
    if (!appState.connecting) return;
    appState.connection.connectGeneration++;
    appState.connection.connectController?.abort();
    appState.connection.connectController = null;
    appState.connecting = false;
    $('.connection-chip').classList.remove('connected');
    $('#connectionStatus').textContent = '已取消连接';
    setBusy(false);
    syncConnectionButton();
    setStatus('已取消服务器连接。');
    showToast('已取消服务器连接。');
  }

  function toggleConnection() {
    if (appState.connecting) return cancelConnect();
    return isConnected() ? disconnectServer() : connectServer();
  }

  function openRawEditor() {
    if (!appState.stickerEditingEnabled) return;
    $('#rawCatalog').value = appState.originalCatalog;
    $('#rawManifest').value = appState.originalManifest;
    appState.dirtyRaw = false; setDirtyState();
    $('#rawDialog').showModal();
  }

  async function saveRawEditor() {
    const catalog = $('#rawCatalog').value;
    const manifest = $('#rawManifest').value;
    appState.sticker.rawSaveController?.abort();
    const controller = new AbortController();
    const generation = ++appState.sticker.rawSaveGeneration;
    appState.sticker.rawSaveController = controller;
    try {
      JSON.parse(catalog);
      const before = 'catalog.json\n' + appState.originalCatalog + '\n\n----- MANIFEST.md -----\n' + appState.originalManifest;
      const after = 'catalog.json\n' + catalog + '\n\n----- MANIFEST.md -----\n' + manifest;
      const okay = await review('保存表情包原始标签文件', before, after);
      if (!okay) return;
      const result = await bridgeCall('saveStickerRaw', { catalog: catalog, manifest: manifest }, { signal: controller.signal });
      if (generation !== appState.sticker.rawSaveGeneration || controller.signal.aborted) return;
      appState.originalCatalog = result.catalogText || catalog; appState.originalManifest = result.manifestText || manifest;
      if (result.stickerFiles) appState.stickerRows = result.stickerFiles;
      appState.stickerEditingEnabled = Boolean(result.stickerEditingEnabled);
      appState.originalStickerRows = snapshotStickerRows();
      appState.dirtyRaw = false; appState.dirtyStickers = false;
      const raw = $('#rawDialog'); raw.close('saved');
      renderStickerList();
      $('#saveStickerButton').disabled = !appState.stickerEditingEnabled; $('#rawStickerButton').disabled = !appState.stickerEditingEnabled;
      setDirtyState();
      setStatus(result.changed ? '原始标签文件已同步保存；已生成加密回滚快照。' : '原始文件没有变化。');
      showToast('原始标签文件已保存。');

    } catch (error) {
      if (!controller.signal.aborted && !(error && error.name === 'AbortError')) reportError(error);
    } finally {
      if (appState.sticker.rawSaveController === controller) appState.sticker.rawSaveController = null;
    }
  }

  async function boot() {
    appState.bridge = window.OpenClawBridge.create({
      onBusy: busy => setBusy(busy),
      onTask: task => {
        if (task.state === 'queued') {
          const ahead = Number(task.queuePosition) || 0;
          setStatus(ahead > 0 ? '任务排队中：前方还有 ' + ahead + ' 个操作。' : '任务正在排队…');
        }
        else if (task.state === 'running') setStatus('正在执行远程操作：' + (task.command || '任务') + '…');
        else if (task.state === 'failed' && task.message) setStatus(task.message, true);
      },
      onBackup: progress => {
        if (progress.phase === 'paused') appState.backupPaused = true;
        renderBackupProgress(progress);
        syncBackupButton();
      }
    });
    await appState.bridge.ready;
    appState.themeController = window.OpenClawTheme.create({ bridgeCall, showToast, reportError });
    appState.stickerCacheController = window.OpenClawStickerCache.create({ bridgeCall });
    appState.memoryController = window.OpenClawMemoryController.create({
      $, bridgeCall, state: appState.memory, sizeLabel, setDirtyState, setStatus, showToast, reportError,
      getThumbnailObserver: () => appState.stickerThumbnailObserver, review
    });
    appState.modelController = window.OpenClawModelController.create({
      $, bridgeCall, state: appState.model, setStatus, showToast, reportError, setBusy,
      isConnected: () => $('.connection-chip').classList.contains('connected')
    });
    appState.stickerController = window.OpenClawStickerController.create({
      $, state: appState.sticker, sizeLabel, setDirtyState, setStatus, showToast, reportError, review,
      bridgeCall, setBusy, syncRenameButton, loadStickerImage, loadStickerThumbnail,
      moveStickerImageCache, getThumbnailObserver: () => appState.stickerThumbnailObserver
    });
    appState.uploadController = window.OpenClawUploadController.create({
      $, bridgeCall, state: appState.upload, sizeLabel, setBusy, setStatus, showToast, reportError,
      renderStickerList, setDirtyState, selectSticker, snapshotStickerRows
    });
    appState.backupController = window.OpenClawBackupController.create({
      $, bridgeCall, state: appState.backup, sizeLabel, etaLabel, setStatus, reportError, showToast,
      syncBackupButton, setBusy, isConnected: () => $('.connection-chip').classList.contains('connected')
    });
    appState.settingsController = window.OpenClawSettingsController.create({
      $, bridgeCall, state: appState.settingsModel, setStatus, showToast, reportError, connectServer
    });
    appState.eventBindings = window.OpenClawEventBindings.create({
      $, $$, appState, switchTab, connectServer, toggleConnection, syncConnectionButton, toggleBackupPause, cancelBackup,
      saveConnectionSettings, bridgeCall, reportError, setStatus, setDirtyState, saveMemory,
      renderMemoryList, renderStickerList, saveStickerRows, openRawEditor,
      testModelLatency, openAddModelDialog, submitAddModel,
      resetThemePalette, uploadFiles, cancelUpload, openRenameDialog, submitStickerRename,
      loadStickerThumbnail, hasDraggedFiles, getDroppedFiles, saveRawEditor,
      setBackdropValue, resetBackdrop, showToast, persistTheme
    });
    appState.eventBindings.wireEvents(); initializeBackdrop();
    let initialized = false;
    try {
      const state = await bridgeCall('initialize', {});
      fillSettings(state.settings.connection, state.settings.backupRetentionCount);
      $('#privatePath').textContent = state.privateDirectory;
      $('#backupPath').textContent = state.backupDirectory;
      applyTheme(state.settings.themeName || 'Atri');
      setStatus('私密数据目录已就绪：' + state.privateDirectory);
      initialized = true;
    } catch (error) { reportError(error); }
    setBusy(false);
    syncConnectionButton();
    if (initialized) await connectServer();
  }

  document.addEventListener('DOMContentLoaded', () => {
    boot().catch(error => {
      const message = error && error.message ? error.message : String(error);
      console.error('[OpenClaw Debugger] UI 初始化失败', error);
      document.body.dataset.bootError = 'true';
      const status = document.querySelector('#statusText');
      if (status) {
        status.textContent = '界面初始化失败：' + message;
        status.classList.add('error-text');
      }
      const toast = document.querySelector('#toast');
      if (toast) {
        toast.textContent = '界面初始化失败，请重新启动调试器。';
        toast.classList.add('error', 'show');
      }
    });
  });
})();
