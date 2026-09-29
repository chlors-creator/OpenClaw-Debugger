(function () {
  'use strict';

  function create(container, options = {}) {
    const rowHeight = Math.max(32, Number(options.rowHeight) || 64);
    const threshold = Math.max(0, Number(options.threshold) || 80);
    const overscan = Math.max(2, Number(options.overscan) || 6);
    let items = [];
    let createRow = null;
    let updateRow = null;
    let frame = 0;
    let virtual = false;
    const content = document.createElement('div');
    content.className = 'virtual-list-content';

    function schedule() {
      if (frame) return;
      frame = window.requestAnimationFrame(() => { frame = 0; renderVirtual(); });
    }

    function renderVirtual() {
      if (!virtual || !createRow) return;
      if (!content.isConnected) container.replaceChildren(content);
      const viewportHeight = container.clientHeight || 560;
      const start = Math.max(0, Math.floor(container.scrollTop / rowHeight) - overscan);
      const end = Math.min(items.length, Math.ceil((container.scrollTop + viewportHeight) / rowHeight) + overscan);
      content.style.height = `${items.length * rowHeight}px`;
      content.replaceChildren();
      for (let index = start; index < end; index++) {
        const row = createRow(items[index], index);
        if (!row) continue;
        if (updateRow) updateRow(row, items[index], index);
        row.classList.add('virtual-list-row');
        row.style.position = 'absolute';
        row.style.left = '0';
        row.style.right = '0';
        row.style.top = `${index * rowHeight}px`;
        row.style.minHeight = `${rowHeight}px`;
        content.appendChild(row);
      }
    }

    function renderNormal() {
      const existing = new Map([...container.children]
        .filter(row => row.dataset.virtualKey)
        .map(row => [row.dataset.virtualKey, row]));
      const fragment = document.createDocumentFragment();
      items.forEach((item, index) => {
        const key = String(item && (item.id || item.imagePath || index)).toLowerCase();
        const row = existing.get(key) || createRow(item, index);
        if (!row) return;
        row.dataset.virtualKey = key;
        if (updateRow) updateRow(row, item, index);
        fragment.appendChild(row);
      });
      container.replaceChildren(fragment);
    }

    container.addEventListener('scroll', schedule, { passive: true });

    return {
      setItems(nextItems, rowFactory, rowUpdater) {
        items = Array.isArray(nextItems) ? nextItems : [];
        createRow = rowFactory;
        updateRow = rowUpdater || null;
        virtual = items.length > threshold;
        container.classList.toggle('virtual-list-active', virtual);
        if (!items.length) { container.replaceChildren(); return; }
        if (virtual) { schedule(); return; }
        renderNormal();
      },
      clear() {
        items = [];
        virtual = false;
        container.classList.remove('virtual-list-active');
        if (frame) { window.cancelAnimationFrame(frame); frame = 0; }
        container.replaceChildren();
      },
      refresh() { if (virtual) schedule(); }
    };
  }

  window.OpenClawVirtualList = { create };
})();
