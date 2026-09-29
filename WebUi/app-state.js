(function () {
  'use strict';

  function alias(target, name, source, key) {
    Object.defineProperty(target, name, {
      configurable: true,
      enumerable: true,
      get: () => source[key],
      set: value => { source[key] = value; }
    });
  }

  function create() {
    const connection = window.OpenClawConnectionState.create();
    const memory = window.OpenClawMemoryState.create();
    const model = window.OpenClawModelState.create();
    const sticker = window.OpenClawStickerState.create();
    const backup = window.OpenClawBackupState.create();
    const settings = window.OpenClawSettingsState.create();
    const state = {
      connection,
      memory,
      model,
      sticker,
      backup,
      settings,
      upload: {
        controller: null,
        get inProgress() { return sticker.uploadInProgress; },
        set inProgress(value) { sticker.uploadInProgress = Boolean(value); },
        get dirtyMemory() { return memory.dirty; },
        set dirtyMemory(value) { memory.dirty = Boolean(value); },
        get dirtyStickers() { return sticker.dirty; },
        set dirtyStickers(value) { sticker.dirty = Boolean(value); },
        get dirtyRaw() { return sticker.rawDirty; },
        set dirtyRaw(value) { sticker.rawDirty = Boolean(value); },
        get rows() { return sticker.rows; },
        set rows(value) { sticker.rows = value || []; },
        get catalog() { return sticker.catalog; },
        set catalog(value) { sticker.catalog = value || ''; },
        get manifest() { return sticker.manifest; },
        set manifest(value) { sticker.manifest = value || ''; },
        get originalRows() { return sticker.originalRows; },
        set originalRows(value) { sticker.originalRows = value || []; },
        get editingEnabled() { return sticker.editingEnabled; },
        set editingEnabled(value) { sticker.editingEnabled = Boolean(value); }
      },
      settingsModel: {
        get value() { return settings.value; },
        set value(value) { settings.value = value || settings.value; }
      },
      themeController: null,
      stickerCacheController: null,
      memoryController: null,
      modelController: null,
      stickerController: null,
      uploadController: null,
      backupController: null,
      settingsController: null,
      eventBindings: null
    };

    alias(state, 'bridge', connection, 'bridge');
    alias(state, 'currentTab', connection, 'currentTab');
    alias(state, 'connecting', connection, 'connecting');
    alias(state, 'toastTimer', connection, 'toastTimer');
    alias(state, 'dirtySyncTimer', connection, 'dirtySyncTimer');
    alias(state, 'stickerThumbnailObserver', connection, 'stickerThumbnailObserver');
    alias(state, 'memoryFiles', memory, 'files');
    alias(state, 'currentMemory', memory, 'current');
    alias(state, 'memoryOriginal', memory, 'original');
    alias(state, 'memoryEditing', memory, 'editing');
    alias(state, 'dirtyMemory', memory, 'dirty');
    alias(state, 'stickerRows', sticker, 'rows');
    alias(state, 'currentSticker', sticker, 'current');
    alias(state, 'stickerEditingEnabled', sticker, 'editingEnabled');
    alias(state, 'originalCatalog', sticker, 'catalog');
    alias(state, 'originalManifest', sticker, 'manifest');
    alias(state, 'originalStickerRows', sticker, 'originalRows');
    alias(state, 'dirtyStickers', sticker, 'dirty');
    alias(state, 'dirtyRaw', sticker, 'rawDirty');
    alias(state, 'stickerPreviewLoading', sticker, 'previewLoading');
    alias(state, 'renameInProgress', sticker, 'renameInProgress');
    alias(state, 'uploadInProgress', sticker, 'uploadInProgress');
    alias(state, 'lastBackupProgress', backup, 'lastProgress');
    alias(state, 'backupActive', backup, 'active');
    alias(state, 'backupPaused', backup, 'paused');
    alias(state, 'backupCancelRequested', backup, 'cancelRequested');
    alias(state, 'backupControlsClosing', backup, 'controlsClosing');
    return state;
  }

  window.OpenClawAppState = { create };
})();
