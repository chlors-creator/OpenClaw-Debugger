(function () {
  'use strict';
  window.OpenClawStickerCache = {
    create(deps) {
      const $ = (selector, root) => (root || document).querySelector(selector);
      const stickerImageCache = new Map();
      const stickerImageReads = new Map();
      const stickerCacheLimit = 96 * 1024 * 1024;
      let stickerCacheBytes = 0;
      const stickerThumbnailCache = new Map();
      const stickerThumbnailReads = new Map();
      const stickerThumbnailCacheLimit = 16 * 1024 * 1024;
      let stickerThumbnailCacheBytes = 0;
function rememberStickerImage(path, preview) {
    const size = Number(preview.size) || Math.ceil((preview.dataUrl || '').length * 0.75);
    const previous = stickerImageCache.get(path);
    if (previous) stickerCacheBytes -= previous.size;
    if (size > stickerCacheLimit) { stickerImageCache.delete(path); return preview; }
    stickerImageCache.delete(path);
    stickerImageCache.set(path, { dataUrl: preview.dataUrl, size: size });
    stickerCacheBytes += size;
    while (stickerCacheBytes > stickerCacheLimit && stickerImageCache.size) {
      const oldestKey = stickerImageCache.keys().next().value;
      const oldest = stickerImageCache.get(oldestKey);
      stickerCacheBytes -= oldest.size;
      stickerImageCache.delete(oldestKey);
    }
    return preview;
  }

  function clearStickerImageCache() {
    stickerImageCache.clear(); stickerImageReads.clear(); stickerCacheBytes = 0;
    stickerThumbnailCache.clear(); stickerThumbnailReads.clear(); stickerThumbnailCacheBytes = 0;
  }

  function moveStickerImageCache(oldPath, newPath) {
    const cached = stickerImageCache.get(oldPath);
    if (cached) {
      stickerImageCache.delete(oldPath);
      stickerImageCache.set(newPath, cached);
    }
    const thumbnail = stickerThumbnailCache.get(oldPath);
    if (thumbnail) {
      stickerThumbnailCache.delete(oldPath);
      stickerThumbnailCache.set(newPath, thumbnail);
    }
  }

  function rememberStickerThumbnail(path, preview) {
    const size = Number(preview.size) || Math.ceil((preview.dataUrl || '').length * 0.75);
    const previous = stickerThumbnailCache.get(path);
    if (previous) stickerThumbnailCacheBytes -= previous.size;
    if (size > stickerThumbnailCacheLimit) { stickerThumbnailCache.delete(path); return preview; }
    stickerThumbnailCache.delete(path);
    stickerThumbnailCache.set(path, { dataUrl: preview.dataUrl, size: size, originalSize: preview.originalSize });
    stickerThumbnailCacheBytes += size;
    while (stickerThumbnailCacheBytes > stickerThumbnailCacheLimit && stickerThumbnailCache.size) {
      const oldestKey = stickerThumbnailCache.keys().next().value;
      const oldest = stickerThumbnailCache.get(oldestKey);
      stickerThumbnailCacheBytes -= oldest.size;
      stickerThumbnailCache.delete(oldestKey);
    }
    return preview;
  }

  function loadStickerImage(path, options) {
    const cached = stickerImageCache.get(path);
    if (cached) {
      stickerImageCache.delete(path); stickerImageCache.set(path, cached);
      return Promise.resolve(cached);
    }
    if (stickerImageReads.has(path)) return stickerImageReads.get(path);
    const read = deps.bridgeCall('readSticker', { path: path }, options).then(preview => rememberStickerImage(path, preview))
      .finally(() => stickerImageReads.delete(path));
    stickerImageReads.set(path, read);
    return read;
  }

  function loadStickerThumbnailData(path) {
    const cached = stickerThumbnailCache.get(path);
    if (cached) {
      stickerThumbnailCache.delete(path); stickerThumbnailCache.set(path, cached);
      return Promise.resolve(cached);
    }
    if (stickerThumbnailReads.has(path)) return stickerThumbnailReads.get(path);
    const read = deps.bridgeCall('readStickerThumbnail', { path: path }).then(preview => rememberStickerThumbnail(path, preview))
      .finally(() => stickerThumbnailReads.delete(path));
    stickerThumbnailReads.set(path, read);
    return read;
  }

  async function loadStickerThumbnail(row, image) {
    try {
      const preview = await loadStickerThumbnailData(row.imagePath);
      if (!image.isConnected) return;
      image.src = preview.dataUrl;
      image.parentElement.classList.add('has-image');
    } catch (_) {
      if (image.isConnected) image.parentElement.classList.add('thumbnail-unavailable');
    }
  }
      return { clearStickerImageCache, moveStickerImageCache, loadStickerImage, loadStickerThumbnailData, loadStickerThumbnail };
    }
  };
})();
