(function () {
  'use strict';
  function create(deps) {
    const { $, bridgeCall, state, sizeLabel, etaLabel, setStatus, reportError, showToast, syncBackupButton, setBusy, isConnected } = deps;
  function renderBackupProgress(data) {
    state.lastProgress = data;
    const panel = $('#backupProgress');
    const phase = data.phase || 'preparing';
    const bytes = Math.max(0, Number(data.bytes) || 0);
    const total = data.totalBytes === null || data.totalBytes === undefined ? null : Math.max(0, Number(data.totalBytes) || 0);
    const speed = Number(data.bytesPerSecond) || 0;
    const attempt = Math.max(1, Number(data.attempt) || 1);
    const maxAttempts = Math.max(1, Number(data.maxAttempts) || 1);
    const attemptLabel = attempt + '/' + maxAttempts;
    panel.hidden = false;
    panel.dataset.phase = phase;
    $('#backupTransferred').textContent = sizeLabel(bytes);
    $('#backupSpeed').textContent = speed > 0 ? sizeLabel(speed) + '/s' : '—';
    $('#backupTotalSize').textContent = total > 0 ? sizeLabel(total) : '准备中';

    const track = $('#backupProgressTrack');
    const bar = $('#backupProgressBar');
    if (phase === 'preparing') {
      $('#backupProgressTitle').textContent = attempt > 1 ? '重试生成服务器快照 · ' + attemptLabel : '正在服务器端生成快照';
      $('#backupProgressDetail').textContent = data.message || '正在把服务器所有文件整理为可断点读取的归档';
      $('#backupProgressPercent').textContent = '准备中';
      $('#backupSpeed').textContent = '未开始';
      $('#backupEta').textContent = '准备中';
      track.setAttribute('aria-busy', 'true');
      track.removeAttribute('aria-valuenow');
      bar.style.width = '';
      return;
    }

    if (phase === 'paused') {
      const percent = total > 0 ? Math.min(99, Math.floor(bytes / total * 100)) : 0;
      $('#backupProgressTitle').textContent = '备份已暂停';
      $('#backupProgressDetail').textContent = data.message || '本地临时归档和 SSH 数据流已保留';
      $('#backupProgressPercent').textContent = percent + '%';
      $('#backupSpeed').textContent = '已暂停';
      $('#backupEta').textContent = '等待继续';
      track.removeAttribute('aria-busy');
      track.setAttribute('aria-valuenow', String(percent));
      bar.style.width = percent + '%';
      return;
    }

    if (phase === 'retrying') {
      $('#backupProgressTitle').textContent = '网络波动，正在自动重试 · ' + attemptLabel;
      $('#backupProgressDetail').textContent = data.message || 'SSH 连接中断，保留已传输内容后等待恢复';
      const retainedPercent = total > 0 ? Math.min(99, Math.floor(bytes / total * 100)) : 0;
      $('#backupProgressPercent').textContent = retainedPercent + '%';
      $('#backupTransferred').textContent = sizeLabel(bytes);
      $('#backupSpeed').textContent = '连接恢复中';
      $('#backupEta').textContent = '等待重试';
      track.setAttribute('aria-busy', 'true');
      track.setAttribute('aria-valuenow', String(retainedPercent));
      bar.style.width = retainedPercent + '%';
      return;
    }
    if (phase === 'cancelling') {
      const percent = total > 0 ? Math.min(99, Math.floor(bytes / total * 100)) : 0;
      $('#backupProgressTitle').textContent = '正在取消服务器备份';
      $('#backupProgressDetail').textContent = data.message || '正在终止 SSH 数据流并清理临时文件';
      $('#backupProgressPercent').textContent = percent + '%';
      $('#backupSpeed').textContent = '正在停止';
      $('#backupEta').textContent = '清理中';
      track.setAttribute('aria-busy', 'true');
      track.setAttribute('aria-valuenow', String(percent));
      bar.style.width = percent + '%';
      return;
    }

    if (phase === 'cancelled') {
      $('#backupProgressTitle').textContent = '备份已取消';
      $('#backupProgressDetail').textContent = data.message || '临时文件已清理，可以重新开始备份';
      $('#backupProgressPercent').textContent = '已取消';
      $('#backupSpeed').textContent = '—';
      $('#backupEta').textContent = '可重新备份';
      track.removeAttribute('aria-busy');
      track.setAttribute('aria-valuenow', '0');
      bar.style.width = '0%';
      return;
    }

    track.removeAttribute('aria-busy');
    if (phase === 'transferring') {
      const percent = total > 0 ? Math.min(99, Math.floor(bytes / total * 100)) : 0;
      $('#backupProgressTitle').textContent = '正在传输服务器快照';
      $('#backupProgressDetail').textContent = 'SSH 连接 · 第 ' + attemptLabel + ' 次';
      $('#backupProgressPercent').textContent = percent + '%';
      $('#backupEta').textContent = etaLabel(data.remainingSeconds);
      track.setAttribute('aria-valuenow', String(percent));
      bar.style.width = percent + '%';
      return;
    }

    if (phase === 'failed') {
      $('#backupProgressTitle').textContent = '备份已中断';
      $('#backupProgressDetail').textContent = data.message || '自动重试已结束；网络恢复后可再次点击“备份服务器”';
      $('#backupProgressPercent').textContent = '已停止';
      $('#backupTransferred').textContent = '临时数据已清理';
      $('#backupSpeed').textContent = '—';
      $('#backupEta').textContent = '可重新备份';
      track.setAttribute('aria-valuenow', '0');
      bar.style.width = '0%';
      setStatus($('#backupProgressDetail').textContent, true);
      return;
    }

    const completed = phase === 'completed';
    const finalizing = phase === 'finalizing';
    const finalPercent = completed ? 100 : 99;
    $('#backupProgressTitle').textContent = completed ? '服务器快照已完成' : '正在保存快照与校验清单';
    $('#backupProgressDetail').textContent = completed ? '归档和 SHA-256 清单已写入本机' : '数据传输完成，正在刷新文件并写入清单';
    $('#backupProgressPercent').textContent = completed ? '100%' : '完成传输';
    $('#backupTotalSize').textContent = sizeLabel(total || bytes);
    $('#backupTransferred').textContent = sizeLabel(bytes);
    $('#backupSpeed').textContent = speed > 0 ? sizeLabel(speed) + '/s' : '—';
    $('#backupEta').textContent = completed ? '已完成' : (finalizing ? '即将完成' : '已完成');
    track.setAttribute('aria-valuenow', String(finalPercent));
    bar.style.width = finalPercent + '%';
    setStatus(completed ? '服务器快照已完成：' + sizeLabel(bytes) : '数据传输完成，正在写入快照清单…');
  }
  async function backupServer() {
    if (!$('.connection-chip').classList.contains('connected')) return;
    if (state.active) return;
    state.controller?.abort();
    const controller = new AbortController();
    state.controller = controller;
    state.generation = Number(state.generation || 0) + 1;
    state.active = true;
    state.paused = false;
    state.cancelRequested = false;
    state.controlsClosing = false;
    syncBackupButton();
    state.lastProgress = null;
    renderBackupProgress({ phase: 'preparing', bytes: 0, totalBytes: null, attempt: 1, maxAttempts: 8 });
    try {
      const result = await bridgeCall('backup', {}, { signal: controller.signal });
      renderBackupProgress({ phase: 'completed', bytes: result.archiveBytes, totalBytes: result.archiveBytes });
      setStatus('整机快照完成：' + sizeLabel(result.archiveBytes) + ' · SHA-256 ' + result.sha256);
      showToast('服务器快照已完成：' + result.directory);
      const okay = window.confirm('服务器快照已完成。\n\n位置：' + result.directory + '\n压缩包：' + result.archivePath + '\n大小：' + sizeLabel(result.archiveBytes) + '\nSHA-256：' + result.sha256 + '\n\n是否打开备份目录？');
      if (okay) await bridgeCall('openBackupFolder', {});
    } catch (error) {
      const last = state.lastProgress || {};
      if (state.cancelRequested) {
        renderBackupProgress({
          phase: 'cancelled',
          bytes: Number(last.bytes) || 0,
          totalBytes: last.totalBytes,
          message: '备份已取消，临时文件已清理。'
        });
        setStatus('服务器备份已取消。');
        showToast('服务器备份已取消。');
      } else if (!controller.signal.aborted) {
        reportError(error);
        renderBackupProgress({ phase: 'failed', bytes: 0, totalBytes: last.totalBytes, message: error && error.message ? error.message : String(error) });
      }
    } finally {
      if (state.controller === controller) state.controller = null;
      state.active = false;
      state.paused = false;
      state.cancelRequested = false;
      state.controlsClosing = false;
      setBusy(false);
      syncBackupButton();
    }
  }

  async function toggleBackupPause() {
    if (!state.active) return backupServer();
    if (state.cancelRequested) return;
    try {
      const result = await bridgeCall('toggleBackupPause', {});
      state.paused = Boolean(result && result.paused);
      syncBackupButton();
    } catch (error) {
      reportError(error);
    }
  }

  async function cancelBackup() {
    if (!state.active || state.cancelRequested) return;
    state.cancelRequested = true;
    state.controlsClosing = true;
    const last = state.lastProgress || {};
    renderBackupProgress({
      phase: 'cancelling',
      bytes: Number(last.bytes) || 0,
      totalBytes: last.totalBytes,
      attempt: Number(last.attempt) || 1,
      maxAttempts: Number(last.maxAttempts) || 8,
      message: '正在取消备份并清理临时文件…'
    });
    syncBackupButton();
    try {
      await bridgeCall('cancelBackup', {});
    } catch (error) {
      state.cancelRequested = false;
      state.controlsClosing = false;
      syncBackupButton();
      reportError(error);
    }
  }

    return { renderProgress: renderBackupProgress, backup: backupServer, togglePause: toggleBackupPause, cancel: cancelBackup };
  }
  window.OpenClawBackupController = { create };
})();
