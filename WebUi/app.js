(function () {
  'use strict';
  const $ = (selector, root) => (root || document).querySelector(selector);
  const $$ = (selector, root) => Array.from((root || document).querySelectorAll(selector));
  const pending = new Map();
  let requestId = 0;
  let toastTimer = 0;
  let dirtySyncTimer = 0;
  let uploadInProgress = false;
  let renameInProgress = false;
  let stickerPreviewLoading = false;
  const stickerImageCache = new Map();
  const stickerImageReads = new Map();
  const stickerCacheLimit = 96 * 1024 * 1024;
  let stickerCacheBytes = 0;
  let stickerThumbnailObserver = null;
  let currentTab = 'overview';
  let settings = { host: '106.14.173.90', username: 'admin', port: 22, workspacePath: '/home/admin/.openclaw/workspace', stickersPath: '/home/admin/.openclaw/workspace/stickers' };
  let memoryFiles = [];
  let currentMemory = null;
  let memoryOriginal = '';
  let memoryEditing = false;
  let stickerRows = [];
  let currentSticker = null;
  let stickerEditingEnabled = false;
  let originalCatalog = '';
  let originalManifest = '';
  let selectedTheme = 'Atri';
  let dirtyMemory = false;
  let dirtyStickers = false;
  let dirtyRaw = false;
  let blurValue = 2;
  let washValue = 25;
  let imageValue = 90;
  const DEFAULT_PALETTES = {
    Atri: { canvas:'#0a1415', surface:'#0f1d1b', ink:'#eef6f2', muted:'#a9bcb4', subtle:'#789087', accent:'#58d7c0', highlight:'#a5e8d4', buttonInk:'#102522', border:'#bee1d0', borderStrong:'#bee1d0', warm:'#efbd75', danger:'#ff8a83', wash:'#f5f9f4', memory:'#7ce0dd', sticker:'#ffa2a4', snapshot:'#f1c777' },
    Luoxi: { canvas:'#1b1219', surface:'#281b24', ink:'#fff2f1', muted:'#d3b9bd', subtle:'#a28189', accent:'#ed8194', highlight:'#f6bdc3', buttonInk:'#311d24', border:'#f4cccc', borderStrong:'#f7c8cc', warm:'#f0ad67', danger:'#ff858c', wash:'#fff4ed', memory:'#89d4ce', sticker:'#ff9aab', snapshot:'#f0c477' },
    Light: { canvas:'#eef3f8', surface:'#fbfdff', ink:'#1e2b36', muted:'#566779', subtle:'#758697', accent:'#397cc8', highlight:'#83afe4', buttonInk:'#ffffff', border:'#455e7a', borderStrong:'#397cc8', warm:'#a46b18', danger:'#bf443f', wash:'#f2f7fc', memory:'#327f8c', sticker:'#bd5969', snapshot:'#a46b18' }
  };
  const COLOR_FIELDS = [
    { key:'canvas', css:'--palette-canvas', label:'界面底色' }, { key:'surface', css:'--palette-surface', label:'卡片与面板' },
    { key:'ink', css:'--palette-ink', label:'主要文字' }, { key:'muted', css:'--palette-muted', label:'次级文字' },
    { key:'subtle', css:'--palette-subtle', label:'弱化文字' }, { key:'accent', css:'--palette-accent', label:'主强调色' },
    { key:'highlight', css:'--palette-highlight', label:'辅助强调色' }, { key:'buttonInk', css:'--palette-button-ink', label:'主按钮文字' },
    { key:'border', css:'--palette-border', label:'边框颜色' }, { key:'borderStrong', css:'--palette-border-strong', label:'高亮边框' },
    { key:'warm', css:'--palette-warm', label:'提示与暖色' }, { key:'danger', css:'--palette-danger', label:'错误与危险' },
    { key:'wash', css:'--palette-wash', label:'背景泛白颜色' }, { key:'memory', css:'--palette-memory', label:'记忆图标色' },
    { key:'sticker', css:'--palette-sticker', label:'表情包图标色' }, { key:'snapshot', css:'--palette-snapshot', label:'备份图标色' }
  ];
  let currentPalette = Object.assign({}, DEFAULT_PALETTES.Atri);

  function bridgeCall(command, payload) {
    return new Promise((resolve, reject) => {
      if (!window.chrome || !window.chrome.webview) return reject(new Error('本地安全桥接不可用，请从桌面应用启动。'));
      const id = String(++requestId);
      pending.set(id, { resolve, reject });
      window.chrome.webview.postMessage({ id: id, command: command, payload: payload || {} });
      window.setTimeout(() => {
        if (!pending.has(id)) return;
        pending.delete(id);
        reject(new Error('操作等待超时，请检查连接后重试。'));
      }, 300000);
    });
  }

  function onBridgeMessage(event) {
    const message = event.data || {};
    if (message.type === 'progress') {
      if (message.command === 'busy') setBusy(Boolean(message.data && message.data.busy));
      if (message.command === 'backup') {
        const bytes = Number(message.data && message.data.bytes) || 0;
        setStatus('正在接收整机快照：' + (bytes / 1048576).toFixed(1) + ' MiB');
      }
      return;
    }
    if (!message.id || !pending.has(String(message.id))) return;
    const operation = pending.get(String(message.id));
    pending.delete(String(message.id));
    if (message.ok) operation.resolve(message.data);
    else operation.reject(new Error(message.error || '操作失败。'));
  }

  function syncRenameButton() {
    const connected = $('.connection-chip').classList.contains('connected');
    const row = stickerRows.find(item => item.imagePath === currentSticker);
    $('#renameStickerButton').disabled = !connected || !row || !stickerEditingEnabled ||
      stickerPreviewLoading || uploadInProgress || renameInProgress || dirtyMemory || dirtyStickers || dirtyRaw;
  }

  function setBusy(busy) {
    const busyNow = Boolean(busy || uploadInProgress || renameInProgress);
    const connected = $('.connection-chip').classList.contains('connected');
    document.body.classList.toggle('busy', busyNow);
    $('#connectButton').disabled = busyNow;
    $('#refreshButton').disabled = busyNow || !connected;
    $('#backupButton').disabled = busyNow || !connected;
    $('#settingsConnectButton').disabled = busyNow;
    $('#saveSettingsButton').disabled = busyNow;
    $('#saveMemoryButton').disabled = busyNow || !memoryEditing || !dirtyMemory;
    $('#saveStickerButton').disabled = busyNow || !stickerEditingEnabled || !dirtyStickers;
    const uploadReady = !busyNow && connected;
    $('#pickStickerButton').disabled = !uploadReady;
    $('#uploadDropzone').classList.toggle('disabled', !uploadReady);
    syncRenameButton();
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
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(() => el.classList.remove('show'), 3400);
  }

  function reportError(error) {
    const message = error && error.message ? error.message : String(error);
    setStatus(message, true);
    showToast(message, true);
  }

  function sizeLabel(size) {
    const value = Number(size) || 0;
    if (value < 1024) return value + ' B';
    if (value < 1048576) return (value / 1024).toFixed(1) + ' KB';
    return (value / 1048576).toFixed(1) + ' MB';
  }

  function switchTab(name) {
    currentTab = name;
    $$('.nav-tab').forEach(button => button.classList.toggle('active', button.dataset.tab === name));
    $$('.page').forEach(page => page.classList.toggle('active', page.id === 'page-' + name));
    const activeButton = $('.nav-tab.active');
    const nav = $('.main-nav');
    if (activeButton) nav.style.setProperty('--active-x', activeButton.offsetLeft + 'px');
  }

  function setDirtyState() {
    const dirty = dirtyMemory || dirtyStickers || dirtyRaw;
    window.clearTimeout(dirtySyncTimer);
    dirtySyncTimer = window.setTimeout(() => bridgeCall('draftState', { dirty: dirty }).catch(() => {}), 90);
    $('#saveMemoryButton').disabled = !memoryEditing || !dirtyMemory;
    $('#saveStickerButton').disabled = !stickerEditingEnabled || !dirtyStickers;
    $('#memoryEditorState').textContent = dirtyMemory ? '有未保存修改' : (memoryEditing ? '编辑模式' : '只读预览');
    syncRenameButton();
  }

  function getConnectionSettings() {
    return {
      host: $('#hostInput').value.trim(), username: $('#usernameInput').value.trim(),
      port: Number($('#portInput').value), workspacePath: $('#workspaceInput').value.trim(),
      stickersPath: $('#stickersInput').value.trim()
    };
  }

  function fillSettings(value) {
    settings = value || settings;
    $('#hostInput').value = settings.host || '106.14.173.90';
    $('#usernameInput').value = settings.username || 'admin';
    $('#portInput').value = settings.port || 22;
    $('#workspaceInput').value = settings.workspacePath || '/home/admin/.openclaw/workspace';
    $('#stickersInput').value = settings.stickersPath || '/home/admin/.openclaw/workspace/stickers';
    $('#targetSummary').textContent = (settings.username || 'admin') + '@' + (settings.host || '106.14.173.90');
  }

  function normalizeTheme(theme) {
    return theme === 'Luoxi' || theme === '洛茜' ? 'Luoxi' : theme === 'Light' || theme === '浅色' ? 'Light' : 'Atri';
  }

  function loadThemePalette(theme) {
    const base = Object.assign({}, DEFAULT_PALETTES[theme]);
    try {
      const saved = JSON.parse(localStorage.getItem('ocd-colors-' + theme) || '{}');
      COLOR_FIELDS.forEach(field => { if (typeof saved[field.key] === 'string' && /^#[0-9a-f]{6}$/i.test(saved[field.key])) base[field.key] = saved[field.key]; });
    } catch (_) { }
    return base;
  }

  function applyPalette() {
    COLOR_FIELDS.forEach(field => document.documentElement.style.setProperty(field.css, currentPalette[field.key]));
    localStorage.setItem('ocd-colors-' + selectedTheme, JSON.stringify(currentPalette));
    renderColorControls();
    applyBackdrop();
  }

  function renderColorControls() {
    const container = $('#colorControls');
    if (!container) return;
    container.replaceChildren();
    COLOR_FIELDS.forEach(field => {
      const label = document.createElement('label'); label.className = 'color-control';
      const input = document.createElement('input'); input.type = 'color'; input.value = currentPalette[field.key]; input.setAttribute('aria-label', field.label);
      const text = document.createElement('span');
      const name = document.createElement('strong'); name.textContent = field.label;
      const value = document.createElement('code'); value.textContent = currentPalette[field.key].toUpperCase();
      input.addEventListener('input', () => {
        currentPalette[field.key] = input.value.toLowerCase(); value.textContent = currentPalette[field.key].toUpperCase();
        document.documentElement.style.setProperty(field.css, currentPalette[field.key]);
        localStorage.setItem('ocd-colors-' + selectedTheme, JSON.stringify(currentPalette));
        if (field.key === 'wash') applyBackdrop();
      });
      text.append(name, value); label.append(input, text); container.append(label);
    });
  }

  function applyTheme(theme) {
    selectedTheme = normalizeTheme(theme);
    document.body.classList.toggle('theme-light', selectedTheme === 'Light');
    document.body.classList.toggle('theme-luoxi', selectedTheme === 'Luoxi');
    const image = selectedTheme === 'Atri' ? "url('assets/Atri.jpg')" : selectedTheme === 'Luoxi' ? "url('assets/Luoxi.jpg')" : 'none';
    document.documentElement.style.setProperty('--theme-image', image);
    currentPalette = loadThemePalette(selectedTheme);
    applyPalette();
    $$('.theme-option').forEach(button => button.classList.toggle('active', button.dataset.theme === selectedTheme));
  }
  function hexToRgba(hex, opacity) {
    const value = String(hex || '#f5f9f4').replace('#', '');
    const safe = /^[0-9a-f]{6}$/i.test(value) ? value : 'f5f9f4';
    const red = parseInt(safe.slice(0, 2), 16), green = parseInt(safe.slice(2, 4), 16), blue = parseInt(safe.slice(4, 6), 16);
    return 'rgba(' + red + ',' + green + ',' + blue + ',' + opacity + ')';
  }

  function applyBackdrop() {
    document.documentElement.style.setProperty('--backdrop-blur', blurValue + 'px');
    document.documentElement.style.setProperty('--backdrop-opacity', (imageValue / 100).toFixed(2));
    const washOpacity = (washValue / 100).toFixed(2);
    const wash = $('.backdrop-wash');
    wash.style.setProperty('--wash-opacity', washOpacity);
    wash.style.backgroundColor = hexToRgba(currentPalette.wash || DEFAULT_PALETTES.Atri.wash, Number(washOpacity));
    $('#blurOutput').value = blurValue + ' px'; $('#washOutput').value = washValue + '%'; $('#imageOutput').value = imageValue + '%';
    $('#blurOutput').textContent = blurValue + ' px'; $('#washOutput').textContent = washValue + '%'; $('#imageOutput').textContent = imageValue + '%';
    localStorage.setItem('ocd-backdrop', JSON.stringify({ blur: blurValue, wash: washValue, image: imageValue }));
  }
  async function persistTheme(theme) {
    const normalized = theme === 'Luoxi' ? 'Luoxi' : theme === 'Light' ? 'Light' : 'Atri';
    applyTheme(normalized);
    try {
      await bridgeCall('setTheme', { theme: normalized });
      showToast('主题已应用并保存。');
    } catch (error) { reportError(error); }
  }

  function renderMemoryList() {
    const list = $('#memoryList');
    const query = $('#memorySearch').value.trim().toLowerCase();
    const filtered = memoryFiles.filter(file => file.relativePath.toLowerCase().includes(query));
    $('#memoryListCount').textContent = filtered.length;
    if (stickerThumbnailObserver) stickerThumbnailObserver.disconnect();
    list.replaceChildren();
    if (!filtered.length) {
      list.classList.add('empty-state');
      list.textContent = memoryFiles.length ? '没有匹配的文件' : '连接服务器后读取文件';
      return;
    }
    list.classList.remove('empty-state');
    filtered.forEach(file => {
      const button = document.createElement('button');
      button.className = 'file-row' + (currentMemory && currentMemory.path === file.relativePath ? ' selected' : '');
      button.type = 'button';
      const glyph = document.createElement('span'); glyph.className = 'file-glyph'; glyph.textContent = '▤';
      const content = document.createElement('span'); content.style.minWidth = '0';
      const name = document.createElement('span'); name.className = 'file-name'; name.textContent = file.relativePath;
      const sub = document.createElement('span'); sub.className = 'file-sub'; sub.textContent = sizeLabel(file.size) + ' · ' + (file.modifiedLabel || 'Markdown');
      content.append(name, sub); button.append(glyph, content);
      button.addEventListener('click', () => selectMemory(file));
      list.append(button);
    });
  }

  async function selectMemory(file) {
    if (dirtyMemory && !window.confirm('当前记忆修改尚未保存。放弃修改并切换文件吗？')) return;
    dirtyMemory = false; memoryEditing = false; currentMemory = null;
    setDirtyState();
    $('#memoryTitle').textContent = file.relativePath;
    $('#memoryInfo').textContent = '正在读取服务器文件…';
    $('#memoryEditor').value = '';
    $('#memoryEditor').readOnly = true;
    $('#editMemoryButton').disabled = true; $('#cancelMemoryButton').disabled = true;
    renderMemoryList();
    try {
      const content = await bridgeCall('readMemory', { path: file.relativePath });
      currentMemory = { path: content.path, sha256: content.sha256 };
      memoryOriginal = content.text;
      $('#memoryEditor').value = memoryOriginal;
      $('#memoryTitle').textContent = content.path;
      const timestamp = content.modifiedUtc ? new Date(content.modifiedUtc).toLocaleString() : '—';
      $('#memoryInfo').textContent = sizeLabel(content.size) + ' · ' + timestamp + ' · SHA-256 ' + content.sha256;
      $('#editMemoryButton').disabled = false;
      $('#memoryEditorState').textContent = '只读预览';
      setStatus('已读取 ' + content.path + '；当前内容仅保留在应用内存中。');
    } catch (error) { reportError(error); $('#memoryInfo').textContent = '读取失败。'; }
    renderMemoryList();
  }

  function renderStickerList() {
    const list = $('#stickerList');
    const query = $('#stickerSearch').value.trim().toLowerCase();
    const filtered = stickerRows.filter(row => row.imagePath.toLowerCase().includes(query) || (row.tagsText || '').toLowerCase().includes(query));
    $('#stickerListCount').textContent = filtered.length;
    list.replaceChildren();
    if (!filtered.length) {
      list.classList.add('empty-state');
      list.textContent = stickerRows.length ? '没有匹配的图片' : '连接服务器后读取表情包';
      return;
    }
    list.classList.remove('empty-state');
    filtered.forEach(row => {
      const card = document.createElement('div');
      card.className = 'sticker-row' + (currentSticker === row.imagePath ? ' selected' : '');
      const thumb = document.createElement('div'); thumb.className = 'sticker-thumb';
      const fallback = document.createElement('span'); fallback.className = 'sticker-thumb-fallback'; fallback.textContent = fileIsGif(row.imagePath) ? 'GIF' : '☺';
      const thumbnail = document.createElement('img'); thumbnail.className = 'sticker-thumb-image'; thumbnail.alt = ''; thumbnail.decoding = 'async'; thumbnail._stickerRow = row;
      thumb.append(fallback, thumbnail);
      const main = document.createElement('div'); main.className = 'sticker-row-main';
      const title = document.createElement('div'); title.className = 'sticker-row-title'; title.textContent = row.imagePath;
      const meta = document.createElement('div'); meta.className = 'sticker-row-meta'; meta.textContent = row.id + (row.tagsText ? ' · ' + row.tagsText : ' · 无标签');
      const tags = document.createElement('input'); tags.className = 'sticker-tags-input'; tags.type = 'text'; tags.value = row.tagsText || ''; tags.placeholder = row.catalogued && stickerEditingEnabled ? '输入标签…' : '尚未登记目录'; tags.disabled = !stickerEditingEnabled || row.catalogued === false; tags.setAttribute('aria-label', '标签 ' + row.imagePath);
      const controls = document.createElement('div'); controls.className = 'sticker-row-controls'; controls.append(tags);
      const weightLabel = document.createElement('label'); weightLabel.className = 'sticker-weight-control';
      const weightCaption = document.createElement('span'); weightCaption.textContent = '权重';
      const weightInput = document.createElement('input'); weightInput.className = 'sticker-weight-input'; weightInput.type = 'number'; weightInput.min = '0'; weightInput.max = '1000000'; weightInput.step = 'any'; weightInput.required = true; weightInput.value = String(Number.isFinite(Number(row.weight)) ? Number(row.weight) : 1); weightInput.disabled = !stickerEditingEnabled || row.catalogued === false; weightInput.setAttribute('aria-label', '表情包选择权重 ' + row.imagePath); weightInput.title = '0 表示不参与抽取；权重越大，在语境合适的候选中被抽中的概率越高。';
      weightLabel.append(weightCaption, weightInput); controls.append(weightLabel);
      tags.addEventListener('input', () => {
        row.tagsText = tags.value;
        meta.textContent = row.id + (tags.value ? ' · ' + tags.value : ' · 无标签');
        updateStickerDirty();
      });
      weightInput.addEventListener('input', () => {
        const weight = weightInput.valueAsNumber;
        row.weightInvalid = !weightInput.value || !weightInput.validity.valid || !Number.isFinite(weight) || weight < 0 || weight > 1000000;
        if (!row.weightInvalid) row.weight = weight;
        updateStickerDirty();
      });
      card.addEventListener('click', event => { if (!controls.contains(event.target)) selectSticker(row); });
      main.append(title, meta, controls); card.append(thumb, main); list.append(card);
      if (stickerThumbnailObserver) stickerThumbnailObserver.observe(thumbnail); else loadStickerThumbnail(row, thumbnail);
    });
  }

  let originalStickerRows = [];
  function snapshotStickerRows() {
    return stickerRows.map(row => ({ id: row.id, imagePath: row.imagePath, tagsText: row.tagsText || '', weight: Number(row.weight ?? 1) }));
  }
  function updateStickerDirty() {
    dirtyStickers = stickerRows.some((row, index) => row.weightInvalid ||
      (row.tagsText || '') !== (originalStickerRows[index] && originalStickerRows[index].tagsText || '') ||
      Number(row.weight ?? 1) !== Number((originalStickerRows[index] && originalStickerRows[index].weight) ?? 1));
    setDirtyState();
  }
  function fileIsGif(path) { return path.toLowerCase().endsWith('.gif'); }

  function rememberStickerImage(path, preview) {
    const size = Number(preview.size) || Math.ceil((preview.dataUrl || '').length * 0.75);
    const previous = stickerImageCache.get(path);
    if (previous) stickerCacheBytes -= previous.size;
    if (size > stickerCacheLimit) { stickerImageCache.delete(path); return preview; }
    stickerImageCache.delete(path);
    stickerImageCache.set(path, { dataUrl: preview.dataUrl, size: size });
    stickerCacheBytes += size;
    while (stickerCacheBytes > stickerCacheLimit && stickerImageCache.size) {
      const oldestKey = stickerImageCache.keys().next().value;
      const oldest = stickerImageCache.get(oldestKey);
      stickerCacheBytes -= oldest.size;
      stickerImageCache.delete(oldestKey);
    }
    return preview;
  }

  function clearStickerImageCache() {
    stickerImageCache.clear(); stickerImageReads.clear(); stickerCacheBytes = 0;
  }

  function moveStickerImageCache(oldPath, newPath) {
    const cached = stickerImageCache.get(oldPath);
    if (!cached) return;
    stickerImageCache.delete(oldPath);
    stickerImageCache.set(newPath, cached);
  }

  function loadStickerImage(path) {
    const cached = stickerImageCache.get(path);
    if (cached) {
      stickerImageCache.delete(path); stickerImageCache.set(path, cached);
      return Promise.resolve(cached);
    }
    if (stickerImageReads.has(path)) return stickerImageReads.get(path);
    const read = bridgeCall('readSticker', { path: path }).then(preview => rememberStickerImage(path, preview))
      .finally(() => stickerImageReads.delete(path));
    stickerImageReads.set(path, read);
    return read;
  }

  async function loadStickerThumbnail(row, image) {
    try {
      const preview = await loadStickerImage(row.imagePath);
      if (!image.isConnected) return;
      image.src = preview.dataUrl;
      image.parentElement.classList.add('has-image');
    } catch (_) {
      if (image.isConnected) image.parentElement.classList.add('thumbnail-unavailable');
    }
  }

  async function selectSticker(row) {
    currentSticker = row.imagePath;
    stickerPreviewLoading = true;
    renderStickerList();
    syncRenameButton();
    $('#stickerTitle').textContent = row.imagePath;
    $('#selectedTags').textContent = row.tagsText ? row.tagsText : '当前没有标签';
    $('#stickerFormat').textContent = fileIsGif(row.imagePath) ? 'ANIMATED GIF' : row.imagePath.split('.').pop().toUpperCase();
    $('#stickerSize').textContent = '';
    $('#stickerImage').hidden = true;
    $('.placeholder-art').hidden = false;
    $('#stickerStatus').textContent = fileIsGif(row.imagePath) ? '正在读取 GIF 动图…' : '正在读取图片…';
    try {
      const preview = await loadStickerImage(row.imagePath);
      if (currentSticker !== row.imagePath) return;
      $('#stickerImage').src = preview.dataUrl;
      $('#stickerImage').hidden = false;
      $('.placeholder-art').hidden = true;
      $('#stickerSize').textContent = sizeLabel(preview.size);
      $('#stickerStatus').textContent = fileIsGif(row.imagePath) ? 'GIF 已加载并由内嵌浏览器原生播放。' : '图片预览已加载。';
    } catch (error) {
      if (currentSticker === row.imagePath) $('#stickerStatus').textContent = '图片读取失败：' + error.message;
    } finally {
      if (currentSticker === row.imagePath) { stickerPreviewLoading = false; syncRenameButton(); }
    }
  }

  function openRenameDialog() {
    const row = stickerRows.find(item => item.imagePath === currentSticker);
    if (!row || !stickerEditingEnabled) return;
    $('#renameInput').value = row.imagePath;
    $('#renameDialog').showModal();
    $('#renameInput').focus();
    const dot = row.imagePath.lastIndexOf('.');
    $('#renameInput').setSelectionRange(0, dot > 0 ? dot : row.imagePath.length);
  }

  async function submitStickerRename() {
    const oldName = currentSticker;
    const newName = $('#renameInput').value.trim();
    if (!oldName || !newName) { showToast('请输入新的图片文件名。', true); return; }
    if (!/\.(png|jpe?g|gif|webp|bmp)$/i.test(newName)) { showToast('请保留原图片扩展名。', true); return; }
    renameInProgress = true; setBusy(true);
    $('#renameConfirmButton').disabled = true;
    try {
      const result = await bridgeCall('renameSticker', { oldFileName: oldName, newFileName: newName });
      moveStickerImageCache(oldName, result.fileName);
      stickerRows = result.stickerFiles || stickerRows;
      originalStickerRows = snapshotStickerRows();
      if (typeof result.catalogText === 'string') originalCatalog = result.catalogText;
      if (typeof result.manifestText === 'string') originalManifest = result.manifestText;
      currentSticker = result.fileName;
      $('#stickerCount').textContent = result.stickerCount;
      $('#stickerListCount').textContent = stickerRows.length;
      dirtyStickers = false;
      $('#renameDialog').close();
      renderStickerList(); setDirtyState();
      const renamed = stickerRows.find(row => row.imagePath === result.fileName);
      if (renamed) await selectSticker(renamed);
      setStatus('已重命名，并同步更新 catalog.json 与 MANIFEST.md。');
      showToast('表情包重命名完成。');
    } catch (error) { reportError(error); }
    finally { renameInProgress = false; $('#renameConfirmButton').disabled = false; setBusy(false); }
  }

  function rowsPayload() { return stickerRows.map(row => ({ id: row.id, imagePath: row.imagePath, tagsText: row.tagsText || '', weight: Number(row.weight ?? 1) })); }

  function review(title, before, after) {
    $('#reviewTitle').textContent = title;
    $('#reviewBefore').textContent = before.length > 90000 ? before.slice(0, 90000) + '\n…（内容过长，已截断预览）' : before;
    $('#reviewAfter').textContent = after.length > 90000 ? after.slice(0, 90000) + '\n…（内容过长，已截断预览）' : after;
    const dialog = $('#reviewDialog');
    dialog.showModal();
    return new Promise(resolve => dialog.addEventListener('close', () => resolve(dialog.returnValue === 'confirm'), { once: true }));
  }

  async function saveMemory() {
    if (!currentMemory || !dirtyMemory) return;
    const text = $('#memoryEditor').value;
    const okay = await review('保存 ' + currentMemory.path, memoryOriginal, text);
    if (!okay) return;
    $('#saveMemoryButton').disabled = true;
    setStatus('正在检查服务器版本并保存…');
    try {
      const result = await bridgeCall('saveMemory', { path: currentMemory.path, expectedSha256: currentMemory.sha256, text: text });
      currentMemory.sha256 = result.sha256;
      memoryOriginal = text;
      dirtyMemory = false; memoryEditing = false;
      $('#memoryEditor').readOnly = true;
      $('#memoryInfo').textContent = sizeLabel(result.size) + ' · 已保存 · SHA-256 ' + result.sha256;
      $('#editMemoryButton').disabled = false; $('#cancelMemoryButton').disabled = true;
      setDirtyState();
      setStatus('保存完成。原版本已 DPAPI 加密快照：' + result.snapshotId);
      showToast('记忆文件已安全保存。');
    } catch (error) { reportError(error); $('#saveMemoryButton').disabled = false; }
  }

  async function saveStickerRows() {
    if (!dirtyStickers) return;
    if (stickerRows.some(row => row.weightInvalid)) { showToast('权重必须是 0 到 1,000,000 之间的数字。', true); return; }
    try {
      const preview = await bridgeCall('previewStickerRows', { rows: rowsPayload() });
      const okay = await review('保存表情包标签与权重（两份文件）', preview.before, preview.after);
      if (!okay) return;
      $('#saveStickerButton').disabled = true;
      const result = await bridgeCall('saveStickerRows', { rows: rowsPayload() });
      originalCatalog = result.catalogText || originalCatalog;
      originalManifest = result.manifestText || originalManifest;
      if (result.stickerFiles) stickerRows = result.stickerFiles;
      stickerEditingEnabled = Boolean(result.stickerEditingEnabled);
      originalStickerRows = snapshotStickerRows();
      dirtyStickers = false; setDirtyState(); renderStickerList();
      setStatus(result.changed ? '表情包标签与权重已同步保存；生成 ' + result.snapshotCount + ' 份加密快照。' : '表情包设置没有变化。');
      showToast('表情包标签已保存。');
    } catch (error) { reportError(error); $('#saveStickerButton').disabled = false; }
  }

  async function saveConnectionSettings(andConnect) {
    try {
      const result = await bridgeCall('saveSettings', getConnectionSettings());
      fillSettings(result.settings.connection);
      $('#privatePath').textContent = result.privateDirectory;
      $('#backupPath').textContent = result.backupDirectory;
      $('#targetSummary').textContent = settings.username + '@' + settings.host;
      setStatus('设置已保存到仓库外的私密目录。');
      if (andConnect) await connectServer(); else showToast('连接设置已保存。');
    } catch (error) { reportError(error); }
  }

  async function connectServer() {
    if (dirtyMemory || dirtyStickers || dirtyRaw) {
      showToast('请先保存或放弃未完成的修改，再重新扫描。', true);
      return;
    }
    $('#connectionStatus').textContent = '正在连接';
    $('.connection-chip').classList.remove('connected');
    setStatus('正在连接服务器并扫描允许管理的目录…');
    try {
      await bridgeCall('saveSettings', getConnectionSettings());
      const result = await bridgeCall('connect', {});
      clearStickerImageCache();
      memoryFiles = result.memoryFiles || [];
      stickerRows = result.stickerFiles || [];
      originalStickerRows = snapshotStickerRows();
      stickerEditingEnabled = Boolean(result.stickerEditingEnabled);
      originalCatalog = result.catalogText || '';
      originalManifest = result.manifestText || '';
      dirtyMemory = false; dirtyStickers = false; dirtyRaw = false; currentMemory = null; currentSticker = null;
      $('#connectionStatus').textContent = result.connectionStatus;
      $('.connection-chip').classList.add('connected');
      $('#memoryCount').textContent = result.memoryCount;
      $('#stickerCount').textContent = result.stickerCount;
      $('#memoryPageCount').textContent = result.memoryCount;
      $('#stickerListCount').textContent = result.stickerCount;
      $('#rootPathsText').textContent = '工作区：' + result.workspacePath + ' · 表情包：' + result.stickersPath;
      $('#stickerStatus').textContent = result.stickerStatus;
      $('#rawStickerButton').disabled = !stickerEditingEnabled;
      $('#saveStickerButton').disabled = true;
      $('#refreshButton').disabled = false; $('#backupButton').disabled = false;
      setBusy(false);
      renderMemoryList(); renderStickerList(); setDirtyState();
      setStatus('SSH 连接成功，已读取 ' + result.memoryCount + ' 个记忆文档和 ' + result.stickerCount + ' 张图片。');
      showToast('服务器连接成功。');
    } catch (error) {
      $('#connectionStatus').textContent = '连接失败';
      $('.connection-chip').classList.remove('connected');
      $('#backupButton').disabled = true; reportError(error);
    }
  }

  function encodeBase64(buffer) {
    const bytes = new Uint8Array(buffer);
    let binary = '';
    for (let offset = 0; offset < bytes.length; offset += 32768) {
      binary += String.fromCharCode.apply(null, bytes.subarray(offset, Math.min(offset + 32768, bytes.length)));
    }
    return btoa(binary);
  }

  function hasDraggedFiles(dataTransfer) {
    if (!dataTransfer) return false;
    if (dataTransfer.files && dataTransfer.files.length) return true;
    if (dataTransfer.items && Array.from(dataTransfer.items).some(item => item.kind === 'file')) return true;
    return Array.from(dataTransfer.types || []).some(type => String(type).toLowerCase() === 'files' || String(type).toLowerCase().startsWith('image/'));
  }

  function getDroppedFiles(dataTransfer) {
    if (!dataTransfer) return [];
    const direct = Array.from(dataTransfer.files || []);
    if (direct.length) return direct;
    return Array.from(dataTransfer.items || [])
      .filter(item => item.kind === 'file')
      .map(item => { try { return item.getAsFile(); } catch (_) { return null; } })
      .filter(Boolean);
  }

  async function uploadFiles(fileList) {
    if (!$('.connection-chip').classList.contains('connected')) { showToast('请先连接服务器再上传。', true); return; }
    if (dirtyMemory || dirtyStickers || dirtyRaw) { showToast('请先保存或放弃当前修改，再上传表情包。', true); return; }
    const files = Array.from(fileList || []);
    if (!files.length) return;
    if (files.length > 20) { showToast('一次最多上传 20 张图片。', true); return; }
    const allowed = /\.(png|jpe?g|gif|webp|bmp)$/i;
    for (const file of files) {
      if (!allowed.test(file.name)) { showToast('不支持的图片格式：' + file.name, true); return; }
      if (file.size < 1 || file.size > 16 * 1024 * 1024) { showToast('图片需小于等于 16 MiB：' + file.name, true); return; }
    }
    uploadInProgress = true;
    setBusy(true);
    const progress = $('#uploadProgress');
    const registrationFailures = [];
    try {
      for (const file of files) {
        progress.textContent = '准备上传 ' + file.name;
        const started = await bridgeCall('beginStickerUpload', { fileName: file.name, size: file.size });
        try {
          const buffer = await file.arrayBuffer();
          const chunkSize = Number(started.chunkBytes) || 196608;
          for (let offset = 0; offset < buffer.byteLength; offset += chunkSize) {
            const chunk = buffer.slice(offset, Math.min(offset + chunkSize, buffer.byteLength));
            await bridgeCall('appendStickerUpload', { uploadId: started.uploadId, contentBase64: encodeBase64(chunk) });
            const percentage = Math.min(100, Math.round((offset + chunk.byteLength) / file.size * 100));
            progress.textContent = '上传 ' + file.name + ' · ' + percentage + '%';
          }
          const result = await bridgeCall('commitStickerUpload', { uploadId: started.uploadId });
          stickerRows = result.stickerFiles || stickerRows;
          if (typeof result.catalogText === 'string') originalCatalog = result.catalogText;
          if (typeof result.manifestText === 'string') originalManifest = result.manifestText;
          originalStickerRows = snapshotStickerRows();
          stickerEditingEnabled = Boolean(result.stickerEditingEnabled);
          dirtyStickers = false;
          $('#stickerCount').textContent = result.stickerCount;
          $('#stickerListCount').textContent = stickerRows.length;
          $('#saveStickerButton').disabled = !stickerEditingEnabled;
          renderStickerList(); setDirtyState();
          $('#stickerStatus').textContent = result.registered === false ? '图片已上传，但自动登记失败：' + (result.registrationError || '请检查目录文件。') : '图片已上传并自动登记为无标签条目，可直接在目录中添加标签。';
          if (result.registered === false) registrationFailures.push(file.name);
          setStatus('上传完成：' + result.fileName + ' · ' + sizeLabel(result.size));
          progress.textContent = '已上传 ' + file.name;
          const added = stickerRows.find(row => row.imagePath === result.fileName);
          if (added) await selectSticker(added);
        } catch (error) {
          await bridgeCall('cancelStickerUpload', { uploadId: started.uploadId }).catch(() => {});
          throw error;
        }
      }
      if (registrationFailures.length) showToast('图片已上传，但有目录登记失败；请查看表情包状态。', true);
      else showToast(files.length === 1 ? '表情包上传并登记完成。' : '已上传并登记 ' + files.length + ' 张表情包。');
    } catch (error) { reportError(error); progress.textContent = '上传失败'; }
    finally { uploadInProgress = false; setBusy(false); }
  }
  async function backupServer() {
    if (!$('.connection-chip').classList.contains('connected')) return;
    $('#backupButton').disabled = true;
    $('#backupButton').innerHTML = '◌ <span>正在备份…</span>';
    setStatus('正在创建整个服务器根文件系统快照…');
    try {
      const result = await bridgeCall('backup', {});
      setStatus('整机快照完成：' + sizeLabel(result.archiveBytes) + ' · SHA-256 ' + result.sha256);
      showToast('服务器快照已完成：' + result.directory);
      const okay = window.confirm('服务器快照已完成。\n\n位置：' + result.directory + '\n压缩包：' + result.archivePath + '\n大小：' + sizeLabel(result.archiveBytes) + '\nSHA-256：' + result.sha256 + '\n\n是否打开备份目录？');
      if (okay) await bridgeCall('openBackupFolder', {});
    } catch (error) { reportError(error); }
    finally { $('#backupButton').innerHTML = '▣ <span>备份服务器</span>'; $('#backupButton').disabled = false; }
  }

  function openRawEditor() {
    if (!stickerEditingEnabled) return;
    $('#rawCatalog').value = originalCatalog;
    $('#rawManifest').value = originalManifest;
    dirtyRaw = false; setDirtyState();
    $('#rawDialog').showModal();
  }

  async function saveRawEditor() {
    const catalog = $('#rawCatalog').value;
    const manifest = $('#rawManifest').value;
    try {
      JSON.parse(catalog);
      const before = 'catalog.json\n' + originalCatalog + '\n\n----- MANIFEST.md -----\n' + originalManifest;
      const after = 'catalog.json\n' + catalog + '\n\n----- MANIFEST.md -----\n' + manifest;
      const okay = await review('保存表情包原始标签文件', before, after);
      if (!okay) return;
      const result = await bridgeCall('saveStickerRaw', { catalog: catalog, manifest: manifest });
      originalCatalog = result.catalogText || catalog; originalManifest = result.manifestText || manifest;
      if (result.stickerFiles) stickerRows = result.stickerFiles;
      stickerEditingEnabled = Boolean(result.stickerEditingEnabled);
      originalStickerRows = snapshotStickerRows();
      dirtyRaw = false; dirtyStickers = false;
      const raw = $('#rawDialog'); raw.close('saved');
      renderStickerList();
      $('#saveStickerButton').disabled = !stickerEditingEnabled; $('#rawStickerButton').disabled = !stickerEditingEnabled;
      setDirtyState();
      setStatus(result.changed ? '原始标签文件已同步保存；已生成加密回滚快照。' : '原始文件没有变化。');
      showToast('原始标签文件已保存。');

    } catch (error) { reportError(error); }
  }

  function initializeBackdrop() {
    try {
      const saved = JSON.parse(localStorage.getItem('ocd-backdrop') || '{}');
      blurValue = Number.isFinite(saved.blur) ? saved.blur : 2;
      washValue = Number.isFinite(saved.wash) ? saved.wash : 25;
      imageValue = Number.isFinite(saved.image) ? saved.image : 90;
    } catch (_) { blurValue = 2; washValue = 25; imageValue = 90; }
    $('#blurRange').value = blurValue; $('#washRange').value = washValue; $('#imageRange').value = imageValue;
    applyBackdrop();
  }

  function wireEvents() {
    $$('.nav-tab').forEach(button => button.addEventListener('click', () => switchTab(button.dataset.tab)));
    $$('[data-go]').forEach(button => button.addEventListener('click', () => switchTab(button.dataset.go)));
    $('#connectButton').addEventListener('click', connectServer);
    $('#refreshButton').addEventListener('click', connectServer);
    $('#backupButton').addEventListener('click', backupServer);
    $('#settingsConnectButton').addEventListener('click', () => saveConnectionSettings(true));
    $('#saveSettingsButton').addEventListener('click', () => saveConnectionSettings(false));
    $('#openPrivateButton').addEventListener('click', () => bridgeCall('openPrivateFolder', {}).catch(reportError));
    $('#editMemoryButton').addEventListener('click', () => {
      if (!currentMemory) return;
      memoryEditing = true; $('#memoryEditor').readOnly = false;
      $('#cancelMemoryButton').disabled = false; $('#memoryEditor').focus(); setDirtyState();
    });
    $('#cancelMemoryButton').addEventListener('click', () => {
      $('#memoryEditor').value = memoryOriginal; $('#memoryEditor').readOnly = true;
      memoryEditing = false; dirtyMemory = false; $('#cancelMemoryButton').disabled = true;
      setDirtyState(); setStatus('已放弃未保存的记忆修改。');
    });
    $('#memoryEditor').addEventListener('input', () => {
      dirtyMemory = $('#memoryEditor').value !== memoryOriginal;
      setDirtyState();
    });
    $('#saveMemoryButton').addEventListener('click', saveMemory);
    $('#memorySearch').addEventListener('input', renderMemoryList);
    $('#stickerSearch').addEventListener('input', renderStickerList);
    $('#saveStickerButton').addEventListener('click', saveStickerRows);
    $('#rawStickerButton').addEventListener('click', openRawEditor);
    $('#resetColors').addEventListener('click', () => {
      localStorage.removeItem('ocd-colors-' + selectedTheme);
      applyTheme(selectedTheme);
      showToast('已恢复此主题的默认颜色。');
    });
    const uploadZone = $('#uploadDropzone');
    const uploadInput = $('#stickerFilesInput');
    $('#pickStickerButton').addEventListener('click', event => { event.stopPropagation(); if (!$('#pickStickerButton').disabled) uploadInput.click(); });
    uploadZone.addEventListener('click', event => { if (!event.target.closest('button') && !event.target.closest('input') && !uploadZone.classList.contains('disabled')) uploadInput.click(); });
    uploadZone.addEventListener('keydown', event => { if ((event.key === 'Enter' || event.key === ' ') && !uploadZone.classList.contains('disabled')) { event.preventDefault(); uploadInput.click(); } });
    uploadInput.addEventListener('change', () => { uploadFiles(uploadInput.files).finally(() => { uploadInput.value = ''; }); });
    $('#renameStickerButton').addEventListener('click', openRenameDialog);
    $('#renameCancelButton').addEventListener('click', () => $('#renameDialog').close());
    $('#renameCloseButton').addEventListener('click', () => $('#renameDialog').close());
    $('#renameForm').addEventListener('submit', event => { event.preventDefault(); submitStickerRename(); });
    stickerThumbnailObserver = 'IntersectionObserver' in window
      ? new IntersectionObserver(entries => entries.forEach(entry => {
          if (!entry.isIntersecting) return;
          stickerThumbnailObserver.unobserve(entry.target);
          loadStickerThumbnail(entry.target._stickerRow, entry.target);
        }), { root: $('#stickerList'), rootMargin: '100px' })
      : null;
    uploadZone.addEventListener('dragenter', event => { event.preventDefault(); if (hasDraggedFiles(event.dataTransfer)) uploadZone.classList.add('drop-active'); });
    uploadZone.addEventListener('dragover', event => { event.preventDefault(); if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy'; uploadZone.classList.add('drop-active'); });
    uploadZone.addEventListener('dragleave', event => { if (!uploadZone.contains(event.relatedTarget)) uploadZone.classList.remove('drop-active'); });
    uploadZone.addEventListener('drop', event => {
      event.preventDefault(); event.stopPropagation(); uploadZone.classList.remove('drop-active');
      const files = getDroppedFiles(event.dataTransfer);
      if (files.length) uploadFiles(files); else showToast('没有读取到拖入的本地文件，请再试一次。', true);
    });
    document.addEventListener('dragover', event => { if (hasDraggedFiles(event.dataTransfer)) event.preventDefault(); });
    document.addEventListener('drop', event => {
      const files = getDroppedFiles(event.dataTransfer);
      if (!files.length) return;
      event.preventDefault();
      if (currentTab === 'stickers' && !uploadZone.contains(event.target)) uploadFiles(files);
      else if (currentTab !== 'stickers') showToast('请先打开表情包页面再拖入图片。', true);
    });
    $('#rawCatalog').addEventListener('input', () => { dirtyRaw = $('#rawCatalog').value !== originalCatalog || $('#rawManifest').value !== originalManifest; setDirtyState(); });
    $('#rawManifest').addEventListener('input', () => { dirtyRaw = $('#rawCatalog').value !== originalCatalog || $('#rawManifest').value !== originalManifest; setDirtyState(); });
    $('#rawSaveButton').addEventListener('click', event => { event.preventDefault(); saveRawEditor(); });
    $('#rawDialog').addEventListener('close', () => { if ($('#rawDialog').returnValue !== 'saved') { dirtyRaw = false; setDirtyState(); } });
    $('#blurRange').addEventListener('input', event => { blurValue = Number(event.target.value); applyBackdrop(); });
    $('#washRange').addEventListener('input', event => { washValue = Number(event.target.value); applyBackdrop(); });
    $('#imageRange').addEventListener('input', event => { imageValue = Number(event.target.value); applyBackdrop(); });
    $('#resetBackdrop').addEventListener('click', () => { blurValue = 2; washValue = 25; imageValue = 90; $('#blurRange').value = 2; $('#washRange').value = 25; $('#imageRange').value = 90; applyBackdrop(); showToast('背景效果已恢复默认。'); });
    $$('.theme-option').forEach(button => button.addEventListener('click', () => persistTheme(button.dataset.theme)));
    window.addEventListener('beforeunload', event => {
      if (dirtyMemory || dirtyStickers || dirtyRaw) { event.preventDefault(); event.returnValue = ''; }
    });
  }

  async function boot() {
    if (window.chrome && window.chrome.webview) window.chrome.webview.addEventListener('message', onBridgeMessage);
    wireEvents(); initializeBackdrop();
    try {
      const state = await bridgeCall('initialize', {});
      fillSettings(state.settings.connection);
      $('#privatePath').textContent = state.privateDirectory;
      $('#backupPath').textContent = state.backupDirectory;
      applyTheme(state.settings.themeName || 'Atri');
      setStatus('私密数据目录已就绪：' + state.privateDirectory);
    } catch (error) { reportError(error); }
    setBusy(false);
  }

  document.addEventListener('DOMContentLoaded', boot);
})();