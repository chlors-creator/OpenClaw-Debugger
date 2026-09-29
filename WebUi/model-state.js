(function () {
  'use strict';
  window.OpenClawModelState = {
    create() {
      return {
        snapshot: null,
        // 客户端模型清单缓存：连接期间短时间内重复进入模型页或刷新时复用。
        snapshotCachedAt: 0,
        cacheTtlMs: 30000,
        loading: false,
        loadPhase: '',
        loadPhaseTimer: null,
        testing: false,
        saving: false,
        adding: false,
        loadGeneration: 0,
        testGeneration: 0,
        orderGeneration: 0,
        addGeneration: 0,
        loadController: null,
        loadPromise: null,
        testController: null,
        orderController: null,
        addController: null,
        draggedId: null,
        pendingOrder: null
      };
    }
  };
})();

