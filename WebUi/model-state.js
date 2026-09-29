(function () {
  'use strict';
  window.OpenClawModelState = {
    create() {
      return {
        snapshot: null,
        // 模型清单缓存元数据：桌面宿主会先返回私密目录中的持久化缓存，
        // 前端再等待服务器更新并替换当前快照。
        snapshotCachedAt: 0,
        cacheTtlMs: 30000,
        cacheSource: '',
        cacheSavedAtUtc: null,
        loading: false,
        refreshing: false,
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
        refreshController: null,
        refreshPromise: null,
        refreshGeneration: 0,
        testController: null,
        orderController: null,
        addController: null,
        draggedId: null,
        pendingOrder: null
      };
    }
  };
})();

