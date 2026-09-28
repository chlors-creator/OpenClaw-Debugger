(function () {
  'use strict';
  function create(deps) {
    const { $, bridgeCall, state, sizeLabel, setBusy, setStatus, showToast, reportError, renderStickerList, setDirtyState, selectSticker, snapshotStickerRows } = deps;
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
    if (state.dirtyMemory || state.dirtyStickers || state.dirtyRaw) { showToast('请先保存或放弃当前修改，再上传表情包。', true); return; }
    const files = Array.from(fileList || []);
    if (!files.length) return;
    if (files.length > 20) { showToast('一次最多上传 20 张图片。', true); return; }
    const allowed = /\.(png|jpe?g|gif|webp|bmp)$/i;
    for (const file of files) {
      if (!allowed.test(file.name)) { showToast('不支持的图片格式：' + file.name, true); return; }
      if (file.size < 1 || file.size > 16 * 1024 * 1024) { showToast('图片需小于等于 16 MiB：' + file.name, true); return; }
    }
    state.inProgress = true;
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
          state.rows = result.stickerFiles || state.rows;
          if (typeof result.catalogText === 'string') state.catalog = result.catalogText;
          if (typeof result.manifestText === 'string') state.manifest = result.manifestText;
          state.originalRows = snapshotStickerRows();
          state.editingEnabled = Boolean(result.stickerEditingEnabled);
          state.dirtyStickers = false;
          $('#stickerCount').textContent = result.stickerCount;
          $('#stickerListCount').textContent = state.rows.length;
          $('#saveStickerButton').disabled = !state.editingEnabled;
          renderStickerList(); setDirtyState();
          $('#stickerStatus').textContent = result.registered === false ? '图片已上传，但自动登记失败：' + (result.registrationError || '请检查目录文件。') : '图片已上传并自动登记为无标签条目，可直接在目录中添加标签。';
          if (result.registered === false) registrationFailures.push(file.name);
          setStatus('上传完成：' + result.fileName + ' · ' + sizeLabel(result.size));
          progress.textContent = '已上传 ' + file.name;
          const added = state.rows.find(row => row.imagePath === result.fileName);
          if (added) await selectSticker(added);
        } catch (error) {
          await bridgeCall('cancelStickerUpload', { uploadId: started.uploadId }).catch(() => {});
          throw error;
        }
      }
      if (registrationFailures.length) showToast('图片已上传，但有目录登记失败；请查看表情包状态。', true);
      else showToast(files.length === 1 ? '表情包上传并登记完成。' : '已上传并登记 ' + files.length + ' 张表情包。');
    } catch (error) { reportError(error); progress.textContent = '上传失败'; }
    finally { state.inProgress = false; setBusy(false); }
  }

    return { encodeBase64, hasDraggedFiles, getDroppedFiles, uploadFiles };
  }
  window.OpenClawUploadController = { create };
})();
