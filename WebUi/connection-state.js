(function () {
  'use strict';
  window.OpenClawConnectionState = {
    create() {
      return {
        bridge: null,
        currentTab: 'overview',
        connecting: false,
        connectGeneration: 0,
        connectController: null,
        busy: false,
        toastTimer: 0,
        dirtySyncTimer: 0,
        stickerThumbnailObserver: null
      };
    }
  };
})();
