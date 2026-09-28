(function () {
  'use strict';
  function create(deps) {
    const { $, state, sizeLabel, setDirtyState, setStatus, showToast, reportError, review, bridgeCall, setBusy, syncRenameButton, loadStickerImage, loadStickerThumbnail, moveStickerImageCache, getThumbnailObserver } = deps;
  function renderStickerList() {
    const list = $('#stickerList');
    const query = $('#stickerSearch').value.trim().toLowerCase();
    const filtered = state.rows.filter(row => row.imagePath.toLowerCase().includes(query) || (row.tagsText || '').toLowerCase().includes(query));
    $('#stickerListCount').textContent = filtered.length;
    list.replaceChildren();
    if (!filtered.length) {
      list.classList.add('empty-state');
      list.textContent = state.rows.length ? '没有匹配的图片' : '连接服务器后读取表情包';
      return;
    }
    list.classList.remove('empty-state');
    filtered.forEach(row => {
      const card = document.createElement('div');
      card.className = 'sticker-row' + (state.current === row.imagePath ? ' selected' : '');
      const thumb = document.createElement('div'); thumb.className = 'sticker-thumb';
      const fallback = document.createElement('span'); fallback.className = 'sticker-thumb-fallback'; fallback.textContent = fileIsGif(row.imagePath) ? 'GIF' : '☺';
      const thumbnail = document.createElement('img'); thumbnail.className = 'sticker-thumb-image'; thumbnail.alt = ''; thumbnail.decoding = 'async'; thumbnail._stickerRow = row;
      thumb.append(fallback, thumbnail);
      const main = document.createElement('div'); main.className = 'sticker-row-main';
      const title = document.createElement('div'); title.className = 'sticker-row-title'; title.textContent = row.imagePath;
      const meta = document.createElement('div'); meta.className = 'sticker-row-meta'; meta.textContent = row.id + (row.tagsText ? ' · ' + row.tagsText : ' · 无标签');
      const tags = document.createElement('input'); tags.className = 'sticker-tags-input'; tags.type = 'text'; tags.value = row.tagsText || ''; tags.placeholder = row.catalogued && state.editingEnabled ? '输入标签…' : '尚未登记目录'; tags.disabled = !state.editingEnabled || row.catalogued === false; tags.setAttribute('aria-label', '标签 ' + row.imagePath);
      const controls = document.createElement('div'); controls.className = 'sticker-row-controls'; controls.append(tags);
      const weightLabel = document.createElement('label'); weightLabel.className = 'sticker-weight-control';
      const weightCaption = document.createElement('span'); weightCaption.textContent = '权重';
      const weightInput = document.createElement('input'); weightInput.className = 'sticker-weight-input'; weightInput.type = 'number'; weightInput.min = '0'; weightInput.max = '1000000'; weightInput.step = 'any'; weightInput.required = true; weightInput.value = String(Number.isFinite(Number(row.weight)) ? Number(row.weight) : 1); weightInput.disabled = !state.editingEnabled || row.catalogued === false; weightInput.setAttribute('aria-label', '表情包选择权重 ' + row.imagePath); weightInput.title = '0 表示不参与抽取；权重越大，在语境合适的候选中被抽中的概率越高。';
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
      if (getThumbnailObserver()) getThumbnailObserver().observe(thumbnail); else loadStickerThumbnail(row, thumbnail);
    });
  }

  function snapshotStickerRows() {
    return state.rows.map(row => ({ id: row.id, imagePath: row.imagePath, tagsText: row.tagsText || '', weight: Number(row.weight ?? 1) }));
  }
  function updateStickerDirty() {
    state.dirty = state.rows.some((row, index) => row.weightInvalid ||
      (row.tagsText || '') !== (state.originalRows[index] && state.originalRows[index].tagsText || '') ||
      Number(row.weight ?? 1) !== Number((state.originalRows[index] && state.originalRows[index].weight) ?? 1));
    setDirtyState();
  }
  function fileIsGif(path) { return path.toLowerCase().endsWith('.gif'); }


  async function selectSticker(row) {
    state.current = row.imagePath;
    state.previewLoading = true;
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
      if (state.current !== row.imagePath) return;
      $('#stickerImage').src = preview.dataUrl;
      $('#stickerImage').hidden = false;
      $('.placeholder-art').hidden = true;
      $('#stickerSize').textContent = sizeLabel(preview.size);
      $('#stickerStatus').textContent = fileIsGif(row.imagePath) ? 'GIF 已加载并由内嵌浏览器原生播放。' : '图片预览已加载。';
    } catch (error) {
      if (state.current === row.imagePath) $('#stickerStatus').textContent = '图片读取失败：' + error.message;
    } finally {
      if (state.current === row.imagePath) { state.previewLoading = false; syncRenameButton(); }
    }
  }


  function openRenameDialog() {
    const row = state.rows.find(item => item.imagePath === state.current);
    if (!row || !state.editingEnabled) return;
    $('#renameInput').value = row.imagePath;
    $('#renameDialog').showModal();
    $('#renameInput').focus();
    const dot = row.imagePath.lastIndexOf('.');
    $('#renameInput').setSelectionRange(0, dot > 0 ? dot : row.imagePath.length);
  }

  async function submitStickerRename() {
    const oldName = state.current;
    const newName = $('#renameInput').value.trim();
    if (!oldName || !newName) { showToast('请输入新的图片文件名。', true); return; }
    if (!/\.(png|jpe?g|gif|webp|bmp)$/i.test(newName)) { showToast('请保留原图片扩展名。', true); return; }
    state.renameInProgress = true; setBusy(true);
    $('#renameConfirmButton').disabled = true;
    try {
      const result = await bridgeCall('renameSticker', { oldFileName: oldName, newFileName: newName });
      moveStickerImageCache(oldName, result.fileName);
      state.rows = result.stickerFiles || state.rows;
      state.originalRows = snapshotStickerRows();
      if (typeof result.catalogText === 'string') state.catalog = result.catalogText;
      if (typeof result.manifestText === 'string') state.manifest = result.manifestText;
      state.current = result.fileName;
      $('#stickerCount').textContent = result.stickerCount;
      $('#stickerListCount').textContent = state.rows.length;
      state.dirty = false;
      $('#renameDialog').close();
      renderStickerList(); setDirtyState();
      const renamed = state.rows.find(row => row.imagePath === result.fileName);
      if (renamed) await selectSticker(renamed);
      setStatus('已重命名，并同步更新 catalog.json 与 MANIFEST.md。');
      showToast('表情包重命名完成。');
    } catch (error) { reportError(error); }
    finally { state.renameInProgress = false; $('#renameConfirmButton').disabled = false; setBusy(false); }
  }


  function rowsPayload() { return state.rows.map(row => ({ id: row.id, imagePath: row.imagePath, tagsText: row.tagsText || '', weight: Number(row.weight ?? 1) })); }


  async function saveStickerRows() {
    if (!state.dirty) return;
    if (state.rows.some(row => row.weightInvalid)) { showToast('权重必须是 0 到 1,000,000 之间的数字。', true); return; }
    try {
      const preview = await bridgeCall('previewStickerRows', { rows: rowsPayload() });
      const okay = await review('保存表情包标签与权重（两份文件）', preview.before, preview.after);
      if (!okay) return;
      $('#saveStickerButton').disabled = true;
      const result = await bridgeCall('saveStickerRows', { rows: rowsPayload() });
      state.catalog = result.catalogText || state.catalog;
      state.manifest = result.manifestText || state.manifest;
      if (result.stickerFiles) state.rows = result.stickerFiles;
      state.editingEnabled = Boolean(result.stickerEditingEnabled);
      state.originalRows = snapshotStickerRows();
      state.dirty = false; setDirtyState(); renderStickerList();
      setStatus(result.changed ? '表情包标签与权重已同步保存；生成 ' + result.snapshotCount + ' 份加密快照。' : '表情包设置没有变化。');
      showToast('表情包标签已保存。');
    } catch (error) { reportError(error); $('#saveStickerButton').disabled = false; }
  }


    return {
      renderList: renderStickerList,
      select: selectSticker,
      openRename: openRenameDialog,
      submitRename: submitStickerRename,
      rowsPayload,
      snapshotRows: snapshotStickerRows,
      updateDirty: updateStickerDirty,
      fileIsGif
    };
  }
  window.OpenClawStickerController = { create };
})();
