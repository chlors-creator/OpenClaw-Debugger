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

    async function sha256Hex(buffer) {
      if (!window.crypto || !window.crypto.subtle) throw new Error('当前 WebView 不支持上传分块校验。');
      const digest = await window.crypto.subtle.digest('SHA-256', buffer);
      return Array.from(new Uint8Array(digest)).map(value => value.toString(16).padStart(2, '0')).join('');
    }

    function wait(milliseconds, signal) {
      return new Promise((resolve, reject) => {
        if (signal?.aborted) { reject(new DOMException('The operation was aborted.', 'AbortError')); return; }
        const timer = window.setTimeout(resolve, milliseconds);
        signal?.addEventListener('abort', () => { window.clearTimeout(timer); reject(new DOMException('The operation was aborted.', 'AbortError')); }, { once: true });
      });
    }

    function etaText(seconds) {
      if (!Number.isFinite(seconds) || seconds < 0) return '计算中';
      const total = Math.ceil(seconds);
      if (total < 60) return total + ' 秒';
      const minutes = Math.floor(total / 60);
      const remaining = total % 60;
      return minutes + ' 分 ' + remaining + ' 秒';
    }

    async function appendChunkWithRetry(uploadId, offset, chunk, sha256, signal, onRetry) {
      let lastError;
      for (let attempt = 1; attempt <= 3; attempt++) {
        try {
          return await bridgeCall('appendStickerUpload', {
            uploadId,
            offset,
            contentBase64: encodeBase64(chunk),
            sha256
          }, { signal });
        } catch (error) {
          lastError = error;
          if (error && error.name === 'AbortError') throw error;
          if (attempt < 3) {
            onRetry?.(attempt + 1);
            await wait(250 * Math.pow(2, attempt - 1), signal);
          }
        }
      }
      throw lastError || new Error('上传分块失败。');
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

    function setUploadUi(active, text) {
      const zone = $('#uploadDropzone');
      const cancel = $('#cancelUploadButton');
      zone.classList.toggle('uploading', Boolean(active));
      cancel.hidden = !active;
      cancel.disabled = !active || state.cancelRequested;
      if (text) $('#uploadProgress').textContent = text;
    }

    function renderUploadProgress(file, fileIndex, fileCount, received, fileSize, chunkIndex, chunkCount, retryAttempt) {
      const total = Number(state.totalBytes) || fileSize;
      const overall = Math.min(total, (Number(state.transferredBeforeFile) || 0) + received);
      const elapsed = Math.max(0.001, (Date.now() - (state.startedAt || Date.now())) / 1000);
      const speed = overall / elapsed;
      const remaining = speed > 0 ? Math.max(0, (total - overall) / speed) : NaN;
      const percent = total > 0 ? Math.min(100, Math.floor(overall / total * 100)) : 0;
      const retry = retryAttempt && retryAttempt > 1 ? ' · 重试第 ' + retryAttempt + ' 次' : '';
      $('#uploadProgress').textContent = file.name + ' · 文件 ' + (fileIndex + 1) + '/' + fileCount + ' · 分块 ' + chunkIndex + '/' + chunkCount + ' · ' + percent + '% · ' + sizeLabel(speed) + '/s · 剩余 ' + etaText(remaining) + retry;
      state.uploadChunkIndex = chunkIndex;
      state.uploadChunkCount = chunkCount;
    }

    async function cancelUpload() {
      if (!state.inProgress || state.cancelRequested) return;
      state.cancelRequested = true;
      const uploadId = state.uploadId;
      state.controller?.abort();
      setUploadUi(true, '正在取消上传…');
      if (uploadId) await bridgeCall('cancelStickerUpload', { uploadId }).catch(() => {});
      setStatus('表情包上传已取消。');
      showToast('上传已取消。');
    }

    async function uploadFiles(fileList) {
      if (state.inProgress) return;
      if (!$('.connection-chip').classList.contains('connected')) { showToast('请先连接服务器再上传。', true); return; }
      if (state.dirtyMemory || state.dirtyStickers || state.dirtyRaw) { showToast('请先保存或放弃当前修改，再上传表情包。', true); return; }
      const files = Array.from(fileList || []);
      if (!files.length) return;
      if (files.length > 20) { showToast('一次最多上传 20 张图片。', true); return; }
      const allowed = /\.(png|jpe?g|gif|webp|bmp)$/i;
      for (const file of files) {
        if (!allowed.test(file.name)) { showToast('不支持的图片格式：' + file.name, true); return; }
        if (file.size < 1 || file.size > 30 * 1024 * 1024) { showToast('图片需小于等于 30 MiB：' + file.name, true); return; }
      }
      state.inProgress = true;
      state.cancelRequested = false;
      state.uploadId = null;
      state.startedAt = Date.now();
      state.transferredBeforeFile = 0;
      state.totalBytes = files.reduce((sum, file) => sum + file.size, 0);
      const controller = new AbortController();
      state.controller = controller;
      setBusy(true);
      setUploadUi(true, '准备上传…');
      const registrationFailures = [];
      try {
        const completeUpload = async (result, file, fileIndex) => {
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
          state.transferredBeforeFile += file.size;
          state.uploadId = null;
          setStatus('上传完成：' + result.fileName + ' · ' + sizeLabel(result.size));
          $('#uploadProgress').textContent = '已上传 ' + file.name + ' · ' + (fileIndex + 1) + '/' + files.length;
          const added = state.rows.find(row => row.imagePath === result.fileName);
          if (added) await selectSticker(added);
        };
        for (let fileIndex = 0; fileIndex < files.length; fileIndex++) {
          const file = files[fileIndex];
          if (controller.signal.aborted) throw new DOMException('The operation was aborted.', 'AbortError');
          // WebView2 can pass the DOM File as a native file object. The host then
          // opens it with a fixed-size stream, avoiding Base64 and a whole-file
          // arrayBuffer. Older runtimes fall back to the resumable chunk protocol.
          if (typeof window.chrome?.webview?.postMessageWithAdditionalObjects === 'function') {
            $('#uploadProgress').textContent = file.name + ' · 正在流式传输…';
            const result = await bridgeCall('streamStickerUpload', { fileName: file.name, size: file.size }, {
              signal: controller.signal, additionalObjects: [file]
            });
            await completeUpload(result, file, fileIndex);
            continue;
          }
          const started = await bridgeCall('beginStickerUpload', { fileName: file.name, size: file.size }, { signal: controller.signal });
          state.uploadId = started.uploadId;
          try {
            const chunkSize = Number(started.chunkBytes) || 196608;
            const chunkCount = Math.max(1, Math.ceil(file.size / chunkSize));
            for (let offset = 0, chunkIndex = 0; offset < file.size; offset += chunkSize, chunkIndex++) {
              const blob = file.slice(offset, Math.min(offset + chunkSize, file.size));
              const chunk = await blob.arrayBuffer();
              const digest = await sha256Hex(chunk);
              const result = await appendChunkWithRetry(started.uploadId, offset, chunk, digest, controller.signal,
                retryAttempt => renderUploadProgress(file, fileIndex, files.length, offset, file.size, chunkIndex + 1, chunkCount, retryAttempt));
              const received = Number(result && result.receivedBytes) || Math.min(file.size, offset + chunk.byteLength);
              renderUploadProgress(file, fileIndex, files.length, received, file.size, chunkIndex + 1, chunkCount);
            }
            const result = await bridgeCall('commitStickerUpload', { uploadId: started.uploadId }, { signal: controller.signal });
            await completeUpload(result, file, fileIndex);
          } catch (error) {
            await bridgeCall('cancelStickerUpload', { uploadId: started.uploadId }).catch(() => {});
            throw error;
          }
        }
        if (registrationFailures.length) showToast('图片已上传，但有目录登记失败；请查看表情包状态。', true);
        else showToast(files.length === 1 ? '表情包上传并登记完成。' : '已上传并登记 ' + files.length + ' 张表情包。');
      } catch (error) {
        if (state.cancelRequested || (error && error.name === 'AbortError')) {
          $('#uploadProgress').textContent = '上传已取消';
        } else {
          reportError(error); $('#uploadProgress').textContent = '上传失败';
        }
      } finally {
        if (state.controller === controller) state.controller = null;
        state.uploadId = null;
        state.inProgress = false;
        state.cancelRequested = false;
        setBusy(false);
        setUploadUi(false);
      }
    }

    return { encodeBase64, sha256Hex, hasDraggedFiles, getDroppedFiles, uploadFiles, cancelUpload };
  }
  window.OpenClawUploadController = { create };
})();
