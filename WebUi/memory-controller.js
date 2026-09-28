(function () {
  'use strict';
  function create(deps) {
    const { $, bridgeCall, state, sizeLabel, setDirtyState, setStatus, showToast, reportError, getThumbnailObserver, review } = deps;
  function renderMemoryList() {
    const list = $('#memoryList');
    const query = $('#memorySearch').value.trim().toLowerCase();
    const filtered = state.files.filter(file => file.relativePath.toLowerCase().includes(query));
    $('#memoryListCount').textContent = filtered.length;
    if (getThumbnailObserver()) getThumbnailObserver().disconnect();
    list.replaceChildren();
    if (!filtered.length) {
      list.classList.add('empty-state');
      list.textContent = state.files.length ? '没有匹配的文件' : '连接服务器后读取文件';
      return;
    }
    list.classList.remove('empty-state');
    filtered.forEach(file => {
      const button = document.createElement('button');
      button.className = 'file-row' + (state.current && state.current.path === file.relativePath ? ' selected' : '');
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
    if (state.dirty && !window.confirm('当前记忆修改尚未保存。放弃修改并切换文件吗？')) return;
    state.dirty = false; state.editing = false; state.current = null;
    setDirtyState();
    $('#memoryTitle').textContent = file.relativePath;
    $('#memoryInfo').textContent = '正在读取服务器文件…';
    $('#memoryEditor').value = '';
    $('#memoryEditor').readOnly = true;
    $('#editMemoryButton').disabled = true; $('#cancelMemoryButton').disabled = true;
    renderMemoryList();
    try {
      const content = await bridgeCall('readMemory', { path: file.relativePath });
      state.current = { path: content.path, sha256: content.sha256 };
      state.original = content.text;
      $('#memoryEditor').value = state.original;
      $('#memoryTitle').textContent = content.path;
      const timestamp = content.modifiedUtc ? new Date(content.modifiedUtc).toLocaleString() : '—';
      $('#memoryInfo').textContent = sizeLabel(content.size) + ' · ' + timestamp + ' · SHA-256 ' + content.sha256;
      $('#editMemoryButton').disabled = false;
      $('#memoryEditorState').textContent = '只读预览';
      setStatus('已读取 ' + content.path + '；当前内容仅保留在应用内存中。');
    } catch (error) { reportError(error); $('#memoryInfo').textContent = '读取失败。'; }
    renderMemoryList();
  }


  async function saveMemory() {
    if (!state.current || !state.dirty) return;
    const text = $('#memoryEditor').value;
    const okay = await review('保存 ' + state.current.path, state.original, text);
    if (!okay) return;
    $('#saveMemoryButton').disabled = true;
    setStatus('正在检查服务器版本并保存…');
    try {
      const result = await bridgeCall('saveMemory', { path: state.current.path, expectedSha256: state.current.sha256, text: text });
      state.current.sha256 = result.sha256;
      state.original = text;
      state.dirty = false; state.editing = false;
      $('#memoryEditor').readOnly = true;
      $('#memoryInfo').textContent = sizeLabel(result.size) + ' · 已保存 · SHA-256 ' + result.sha256;
      $('#editMemoryButton').disabled = false; $('#cancelMemoryButton').disabled = true;
      setDirtyState();
      setStatus('保存完成。原版本已 DPAPI 加密快照：' + result.snapshotId);
      showToast('记忆文件已安全保存。');
    } catch (error) { reportError(error); $('#saveMemoryButton').disabled = false; }
  }


    return { renderList: renderMemoryList, select: selectMemory, save: saveMemory };
  }
  window.OpenClawMemoryController = { create };
})();
