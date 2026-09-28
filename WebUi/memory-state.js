(function () {
  'use strict';
  window.OpenClawMemoryState = {
    create() {
      return {
        files: [],
        current: null,
        original: '',
        editing: false,
        dirty: false,
        readGeneration: 0,
        readController: null,
        saveController: null
      };
    }
  };
})();
