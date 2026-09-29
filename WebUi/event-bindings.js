(function () {
  'use strict';
  function create(deps) {
    const {
      $, $$, appState, switchTab, connectServer, toggleConnection, syncConnectionButton, toggleBackupPause, cancelBackup,
      saveConnectionSettings, bridgeCall, reportError, setStatus, setDirtyState, saveMemory,
      renderMemoryList, renderStickerList, saveStickerRows, openRawEditor,
      testModelLatency, openAddModelDialog, submitAddModel,
      resetThemePalette, uploadFiles, cancelUpload, openRenameDialog, submitStickerRename,
      loadStickerThumbnail, hasDraggedFiles, getDroppedFiles, saveRawEditor,
      setBackdropValue, resetBackdrop, showToast, persistTheme
    } = deps;
    function wireEvents() {

    $$('.nav-tab').forEach(button => button.addEventListener('click', () => switchTab(button.dataset.tab)));
    $$('[data-go]').forEach(button => button.addEventListener('click', () => switchTab(button.dataset.go)));
    const connectionButton = $('#connectButton');
    connectionButton.addEventListener('click', toggleConnection);
    connectionButton.addEventListener('mouseenter', syncConnectionButton);
    connectionButton.addEventListener('mouseleave', syncConnectionButton);
    connectionButton.addEventListener('focusin', syncConnectionButton);
    connectionButton.addEventListener('focusout', syncConnectionButton);
    $('#refreshButton').addEventListener('click', connectServer);
    $('#backupButton').addEventListener('click', toggleBackupPause);
    $('#cancelBackupButton').addEventListener('click', cancelBackup);
    $('#settingsConnectButton').addEventListener('click', () => saveConnectionSettings(true));
    $('#saveSettingsButton').addEventListener('click', () => saveConnectionSettings(false));
    $('#openPrivateButton').addEventListener('click', () => bridgeCall('openPrivateFolder', {}).catch(reportError));
    $('#exportLogsButton').addEventListener('click', async () => {
      try {
        const result = await bridgeCall('exportLogs', {});
        setStatus('操作日志已导出：' + result.path);
        showToast('操作日志已导出到私密目录。');
        await bridgeCall('openPrivateFolder', {});
      } catch (error) { reportError(error); }
    });
    $('#testModelLatencyButton').addEventListener('click', testModelLatency);
    $('#addModelButton').addEventListener('click', openAddModelDialog);
    $('#modelAddCancelButton').addEventListener('click', () => $('#modelAddDialog').close());
    $('#modelAddCloseButton').addEventListener('click', () => $('#modelAddDialog').close());
    $('#modelAddForm').addEventListener('submit', event => { event.preventDefault(); submitAddModel(); });
    $('#editMemoryButton').addEventListener('click', () => {
      if (!appState.currentMemory) return;
      appState.memoryEditing = true; $('#memoryEditor').readOnly = false;
      $('#cancelMemoryButton').disabled = false; $('#memoryEditor').focus(); setDirtyState();
    });
    $('#cancelMemoryButton').addEventListener('click', () => {
      $('#memoryEditor').value = appState.memoryOriginal; $('#memoryEditor').readOnly = true;
      appState.memoryEditing = false; appState.dirtyMemory = false; $('#cancelMemoryButton').disabled = true;
      setDirtyState(); setStatus('已放弃未保存的记忆修改。');
    });
    $('#memoryEditor').addEventListener('input', () => {
      appState.dirtyMemory = $('#memoryEditor').value !== appState.memoryOriginal;
      setDirtyState();
    });
    $('#saveMemoryButton').addEventListener('click', saveMemory);
    $('#memorySearch').addEventListener('input', renderMemoryList);
    $('#stickerSearch').addEventListener('input', renderStickerList);
    $('#stickerTagFilter').addEventListener('change', renderStickerList);
    $('#saveStickerButton').addEventListener('click', saveStickerRows);
    $('#rawStickerButton').addEventListener('click', openRawEditor);
    $('#resetColors').addEventListener('click', () => {
      resetThemePalette();
      showToast('已恢复此主题的默认颜色。');
    });
    const uploadZone = $('#uploadDropzone');
    const uploadInput = $('#stickerFilesInput');
    $('#pickStickerButton').addEventListener('click', event => { event.stopPropagation(); if (!$('#pickStickerButton').disabled) uploadInput.click(); });
    uploadZone.addEventListener('click', event => { if (!event.target.closest('button') && !event.target.closest('input') && !uploadZone.classList.contains('disabled')) uploadInput.click(); });
    uploadZone.addEventListener('keydown', event => { if ((event.key === 'Enter' || event.key === ' ') && !uploadZone.classList.contains('disabled')) { event.preventDefault(); uploadInput.click(); } });
    uploadInput.addEventListener('change', () => { uploadFiles(uploadInput.files).finally(() => { uploadInput.value = ''; }); });
    $('#cancelUploadButton').addEventListener('click', event => { event.stopPropagation(); cancelUpload(); });
    $('#renameStickerButton').addEventListener('click', openRenameDialog);
    $('#renameCancelButton').addEventListener('click', () => $('#renameDialog').close());
    $('#renameCloseButton').addEventListener('click', () => $('#renameDialog').close());
    $('#renameForm').addEventListener('submit', event => { event.preventDefault(); submitStickerRename(); });
    appState.stickerThumbnailObserver = 'IntersectionObserver' in window
      ? new IntersectionObserver(entries => entries.forEach(entry => {
          if (!entry.isIntersecting) return;
          appState.stickerThumbnailObserver.unobserve(entry.target);
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
      if (appState.currentTab === 'stickers' && !uploadZone.contains(event.target)) uploadFiles(files);
      else if (appState.currentTab !== 'stickers') showToast('请先打开表情包页面再拖入图片。', true);
    });
    $('#rawCatalog').addEventListener('input', () => { appState.dirtyRaw = $('#rawCatalog').value !== appState.originalCatalog || $('#rawManifest').value !== appState.originalManifest; setDirtyState(); });
    $('#rawManifest').addEventListener('input', () => { appState.dirtyRaw = $('#rawCatalog').value !== appState.originalCatalog || $('#rawManifest').value !== appState.originalManifest; setDirtyState(); });
    $('#rawSaveButton').addEventListener('click', event => { event.preventDefault(); saveRawEditor(); });
    $('#rawDialog').addEventListener('close', () => { if ($('#rawDialog').returnValue !== 'saved') { appState.dirtyRaw = false; setDirtyState(); } });
    $('#blurRange').addEventListener('input', event => setBackdropValue('blur', event.target.value));
    $('#washRange').addEventListener('input', event => setBackdropValue('wash', event.target.value));
    $('#imageRange').addEventListener('input', event => setBackdropValue('image', event.target.value));
    $('#resetBackdrop').addEventListener('click', () => { resetBackdrop(); showToast('背景效果已恢复默认。'); });
    $$('.theme-option').forEach(button => button.addEventListener('click', () => persistTheme(button.dataset.theme)));
    window.addEventListener('beforeunload', event => {
      appState.connection.connectController?.abort();
      appState.connection.disconnectController?.abort();
      appState.memory.readController?.abort();
      appState.memory.saveController?.abort();
      appState.sticker.previewController?.abort();
      appState.sticker.saveController?.abort();
      appState.sticker.renameController?.abort();
      appState.sticker.rawSaveController?.abort();
      appState.sticker.uploadController?.abort();
      appState.upload?.controller?.abort();
      appState.model.loadController?.abort();
      appState.model.testController?.abort();
      appState.model.orderController?.abort();
      appState.model.addController?.abort();
      appState.backup.controller?.abort();
      appState.bridge?.cancelAll();
      if (appState.dirtyMemory || appState.dirtyStickers || appState.dirtyRaw) { event.preventDefault(); event.returnValue = ''; }
    });
    }
    return { wireEvents };
  }
  window.OpenClawEventBindings = { create };
})();
