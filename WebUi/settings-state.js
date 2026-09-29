(function () {
  'use strict';
  window.OpenClawSettingsState = {
    create() {
      return {
        value: {
          host: '106.14.173.90',
          username: 'admin',
          port: 22,
          workspacePath: '/home/admin/.openclaw/workspace',
          stickersPath: '/home/admin/.openclaw/workspace/stickers',
          backupRetentionCount: 5
        },
        themeController: null,
        saveController: null
      };
    }
  };
})();
