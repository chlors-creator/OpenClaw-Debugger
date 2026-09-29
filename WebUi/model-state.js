(function () {
  'use strict';
  window.OpenClawModelState = {
    create() {
      return {
        snapshot: null,
        loading: false,
        testing: false,
        saving: false,
        adding: false,
        loadGeneration: 0,
        testGeneration: 0,
        orderGeneration: 0,
        addGeneration: 0,
        loadController: null,
        testController: null,
        orderController: null,
        addController: null,
        draggedId: null,
        pendingOrder: null
      };
    }
  };
})();

