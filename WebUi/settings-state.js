(function () {
  'use strict';
  window.OpenClawSettingsState = {
    create() {
      return {
        value: {
          host: '',
          username: '',
          port: 22,
          workspacePath: '/home/user/.openclaw/workspace',
          stickersPath: '/home/user/.openclaw/workspace/stickers',
          backupRetentionCount: 5
        },
        themeController: null,
        saveController: null
      };
    }
  };
})();
