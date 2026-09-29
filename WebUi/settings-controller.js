(function () {
  'use strict';
  function create(deps) {
    const { $, bridgeCall, state, setStatus, showToast, reportError, connectServer } = deps;
    function getConnectionSettings() {
      return {
        host: $('#hostInput').value.trim(),
        username: $('#usernameInput').value.trim(),
        port: Number($('#portInput').value),
        workspacePath: $('#workspaceInput').value.trim(),
        stickersPath: $('#stickersInput').value.trim(),
        backupRetentionCount: Math.max(1, Math.min(30, Number($('#backupRetentionInput').value) || 5))
      };
    }
    function fillSettings(value, retentionOverride) {
      state.value = value || state.value;
      $('#hostInput').value = state.value.host || '';
      $('#usernameInput').value = state.value.username || '';
      $('#portInput').value = state.value.port || 22;
      $('#backupRetentionInput').value = retentionOverride || state.value.backupRetentionCount || 5;
      $('#workspaceInput').value = state.value.workspacePath || '/home/user/.openclaw/workspace';
      $('#stickersInput').value = state.value.stickersPath || '/home/user/.openclaw/workspace/stickers';
      $('#targetSummary').textContent = state.value.username && state.value.host
        ? state.value.username + '@' + state.value.host
        : '未配置 SSH 目标';
    }
    async function saveConnectionSettings(andConnect) {
      try {
        const result = await bridgeCall('saveSettings', getConnectionSettings());
        fillSettings(result.settings.connection, result.settings.backupRetentionCount);
        $('#privatePath').textContent = result.privateDirectory;
        $('#backupPath').textContent = result.backupDirectory;
        $('#targetSummary').textContent = state.value.username + '@' + state.value.host;
        setStatus('设置已保存到仓库外的私密目录。');
        if (andConnect) await connectServer(); else showToast('连接设置已保存。');
      } catch (error) { reportError(error); }
    }
    return { getConnectionSettings, fillSettings, saveConnectionSettings };
  }
  window.OpenClawSettingsController = { create };
})();
