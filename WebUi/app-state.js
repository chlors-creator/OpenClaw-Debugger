(function () {
  'use strict';

  function create() {
    const state = {
      bridge: null,
      toastTimer: 0,
      dirtySyncTimer: 0,
      uploadInProgress: false,
      renameInProgress: false,
      stickerPreviewLoading: false,
      stickerThumbnailObserver: null,
      currentTab: 'overview',
      settings: {
        host: '106.14.173.90',
        username: 'admin',
        port: 22,
        workspacePath: '/home/admin/.openclaw/workspace',
        stickersPath: '/home/admin/.openclaw/workspace/stickers'
      },
      memoryFiles: [],
      currentMemory: null,
      memoryOriginal: '',
      memoryEditing: false,
      stickerRows: [],
      currentSticker: null,
      stickerEditingEnabled: false,
      originalCatalog: '',
      originalManifest: '',
      dirtyMemory: false,
      dirtyStickers: false,
      dirtyRaw: false,
      originalStickerRows: [],
      lastBackupProgress: null,
      backupActive: false,
      backupPaused: false,
      backupCancelRequested: false,
      backupControlsClosing: false,
      themeController: null,
      stickerCacheController: null,
      memoryController: null,
      stickerController: null,
      uploadController: null,
      backupController: null,
      settingsController: null,
      eventBindings: null
    };

    state.memory = {
      get files() { return state.memoryFiles; }, set files(value) { state.memoryFiles = value || []; },
      get current() { return state.currentMemory; }, set current(value) { state.currentMemory = value; },
      get original() { return state.memoryOriginal; }, set original(value) { state.memoryOriginal = value || ''; },
      get editing() { return state.memoryEditing; }, set editing(value) { state.memoryEditing = Boolean(value); },
      get dirty() { return state.dirtyMemory; }, set dirty(value) { state.dirtyMemory = Boolean(value); }
    };
    state.sticker = {
      get rows() { return state.stickerRows; }, set rows(value) { state.stickerRows = value || []; },
      get current() { return state.currentSticker; }, set current(value) { state.currentSticker = value; },
      get editingEnabled() { return state.stickerEditingEnabled; }, set editingEnabled(value) { state.stickerEditingEnabled = Boolean(value); },
      get catalog() { return state.originalCatalog; }, set catalog(value) { state.originalCatalog = value || ''; },
      get manifest() { return state.originalManifest; }, set manifest(value) { state.originalManifest = value || ''; },
      get originalRows() { return state.originalStickerRows; }, set originalRows(value) { state.originalStickerRows = value || []; },
      get dirty() { return state.dirtyStickers; }, set dirty(value) { state.dirtyStickers = Boolean(value); },
      get previewLoading() { return state.stickerPreviewLoading; }, set previewLoading(value) { state.stickerPreviewLoading = Boolean(value); },
      get renameInProgress() { return state.renameInProgress; }, set renameInProgress(value) { state.renameInProgress = Boolean(value); }
    };
    state.upload = {
      get inProgress() { return state.uploadInProgress; }, set inProgress(value) { state.uploadInProgress = Boolean(value); },
      get dirtyMemory() { return state.dirtyMemory; }, set dirtyMemory(value) { state.dirtyMemory = Boolean(value); },
      get dirtyStickers() { return state.dirtyStickers; }, set dirtyStickers(value) { state.dirtyStickers = Boolean(value); },
      get dirtyRaw() { return state.dirtyRaw; }, set dirtyRaw(value) { state.dirtyRaw = Boolean(value); },
      get rows() { return state.stickerRows; }, set rows(value) { state.stickerRows = value || []; },
      get catalog() { return state.originalCatalog; }, set catalog(value) { state.originalCatalog = value || ''; },
      get manifest() { return state.originalManifest; }, set manifest(value) { state.originalManifest = value || ''; },
      get originalRows() { return state.originalStickerRows; }, set originalRows(value) { state.originalStickerRows = value || []; },
      get editingEnabled() { return state.stickerEditingEnabled; }, set editingEnabled(value) { state.stickerEditingEnabled = Boolean(value); }
    };
    state.backup = {
      get lastProgress() { return state.lastBackupProgress; }, set lastProgress(value) { state.lastBackupProgress = value; },
      get active() { return state.backupActive; }, set active(value) { state.backupActive = Boolean(value); },
      get paused() { return state.backupPaused; }, set paused(value) { state.backupPaused = Boolean(value); },
      get cancelRequested() { return state.backupCancelRequested; }, set cancelRequested(value) { state.backupCancelRequested = Boolean(value); },
      get controlsClosing() { return state.backupControlsClosing; }, set controlsClosing(value) { state.backupControlsClosing = Boolean(value); }
    };
    state.settingsModel = {
      get value() { return state.settings; }, set value(value) { state.settings = value || state.settings; }
    };
    return state;
  }

  window.OpenClawAppState = { create };
})();
