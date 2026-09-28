(function () {
  'use strict';
  window.OpenClawStickerState = {
    create() {
      return {
        rows: [],
        current: null,
        editingEnabled: false,
        catalog: '',
        manifest: '',
        originalRows: [],
        dirty: false,
        rawDirty: false,
        previewLoading: false,
        previewGeneration: 0,
        previewController: null,
        renameInProgress: false,
        renameController: null,
        saveController: null,
        rawSaveController: null,
        rawSaveGeneration: 0,
        uploadInProgress: false,
        uploadController: null
      };
    }
  };
})();
