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
        stickersPath: $('#stickersInput').value.trim()
      };
    }
    function fillSettings(value) {
      state.value = value || state.value;
      $('#hostInput').value = state.value.host || '106.14.173.90';
      $('#usernameInput').value = state.value.username || 'admin';
      $('#portInput').value = state.value.port || 22;
      $('#workspaceInput').value = state.value.workspacePath || '/home/admin/.openclaw/workspace';
      $('#stickersInput').value = state.value.stickersPath || '/home/admin/.openclaw/workspace/stickers';
      $('#targetSummary').textContent = (state.value.username || 'admin') + '@' + (state.value.host || '106.14.173.90');
    }
    async function saveConnectionSettings(andConnect) {
      try {
        const result = await bridgeCall('saveSettings', getConnectionSettings());
        fillSettings(result.settings.connection);
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
