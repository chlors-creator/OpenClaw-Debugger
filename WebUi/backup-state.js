(function () {
  'use strict';
  window.OpenClawBackupState = {
    create() {
      return {
        lastProgress: null,
        active: false,
        paused: false,
        cancelRequested: false,
        controlsClosing: false,
        generation: 0,
        controller: null
      };
    }
  };
})();
