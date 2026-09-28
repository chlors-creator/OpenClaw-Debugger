(function () {
  'use strict';
  window.OpenClawTheme = {
    create(deps) {
      const $ = (selector, root) => (root || document).querySelector(selector);
      const $$ = (selector, root) => Array.from((root || document).querySelectorAll(selector));
      let selectedTheme = 'Atri';
      let blurValue = 2;
      let washValue = 25;
      let imageValue = 90;
const DEFAULT_PALETTES = {
    Atri: { canvas:'#0a1415', surface:'#0f1d1b', ink:'#eef6f2', muted:'#a9bcb4', subtle:'#789087', accent:'#58d7c0', highlight:'#a5e8d4', buttonInk:'#102522', border:'#bee1d0', borderStrong:'#bee1d0', warm:'#efbd75', danger:'#ff8a83', wash:'#f5f9f4', memory:'#7ce0dd', sticker:'#ffa2a4', snapshot:'#f1c777' },
    Luoxi: { canvas:'#1b1219', surface:'#281b24', ink:'#fff2f1', muted:'#d3b9bd', subtle:'#a28189', accent:'#ed8194', highlight:'#f6bdc3', buttonInk:'#311d24', border:'#f4cccc', borderStrong:'#f7c8cc', warm:'#f0ad67', danger:'#ff858c', wash:'#fff4ed', memory:'#89d4ce', sticker:'#ff9aab', snapshot:'#f0c477' },
    Light: { canvas:'#eef3f8', surface:'#fbfdff', ink:'#1e2b36', muted:'#566779', subtle:'#758697', accent:'#397cc8', highlight:'#83afe4', buttonInk:'#ffffff', border:'#455e7a', borderStrong:'#397cc8', warm:'#a46b18', danger:'#bf443f', wash:'#f2f7fc', memory:'#327f8c', sticker:'#bd5969', snapshot:'#a46b18' }
  };
  const COLOR_FIELDS = [
    { key:'canvas', css:'--palette-canvas', label:'界面底色' }, { key:'surface', css:'--palette-surface', label:'卡片与面板' },
    { key:'ink', css:'--palette-ink', label:'主要文字' }, { key:'muted', css:'--palette-muted', label:'次级文字' },
    { key:'subtle', css:'--palette-subtle', label:'弱化文字' }, { key:'accent', css:'--palette-accent', label:'主强调色' },
    { key:'highlight', css:'--palette-highlight', label:'辅助强调色' }, { key:'buttonInk', css:'--palette-button-ink', label:'主按钮文字' },
    { key:'border', css:'--palette-border', label:'边框颜色' }, { key:'borderStrong', css:'--palette-border-strong', label:'高亮边框' },
    { key:'warm', css:'--palette-warm', label:'提示与暖色' }, { key:'danger', css:'--palette-danger', label:'错误与危险' },
    { key:'wash', css:'--palette-wash', label:'背景泛白颜色' }, { key:'memory', css:'--palette-memory', label:'记忆图标色' },
    { key:'sticker', css:'--palette-sticker', label:'表情包图标色' }, { key:'snapshot', css:'--palette-snapshot', label:'备份图标色' }
  ];
  let currentPalette = Object.assign({}, DEFAULT_PALETTES.Atri);
function normalizeTheme(theme) {
    return theme === 'Luoxi' || theme === '洛茜' ? 'Luoxi' : theme === 'Light' || theme === '浅色' ? 'Light' : 'Atri';
  }

  function loadThemePalette(theme) {
    const base = Object.assign({}, DEFAULT_PALETTES[theme]);
    try {
      const saved = JSON.parse(localStorage.getItem('ocd-colors-' + theme) || '{}');
      COLOR_FIELDS.forEach(field => { if (typeof saved[field.key] === 'string' && /^#[0-9a-f]{6}$/i.test(saved[field.key])) base[field.key] = saved[field.key]; });
    } catch (_) { }
    return base;
  }

  function applyPalette() {
    COLOR_FIELDS.forEach(field => document.documentElement.style.setProperty(field.css, currentPalette[field.key]));
    localStorage.setItem('ocd-colors-' + selectedTheme, JSON.stringify(currentPalette));
    renderColorControls();
    applyBackdrop();
  }

  function renderColorControls() {
    const container = $('#colorControls');
    if (!container) return;
    container.replaceChildren();
    COLOR_FIELDS.forEach(field => {
      const label = document.createElement('label'); label.className = 'color-control';
      const input = document.createElement('input'); input.type = 'color'; input.value = currentPalette[field.key]; input.setAttribute('aria-label', field.label);
      const text = document.createElement('span');
      const name = document.createElement('strong'); name.textContent = field.label;
      const value = document.createElement('code'); value.textContent = currentPalette[field.key].toUpperCase();
      input.addEventListener('input', () => {
        currentPalette[field.key] = input.value.toLowerCase(); value.textContent = currentPalette[field.key].toUpperCase();
        document.documentElement.style.setProperty(field.css, currentPalette[field.key]);
        localStorage.setItem('ocd-colors-' + selectedTheme, JSON.stringify(currentPalette));
        if (field.key === 'wash') applyBackdrop();
      });
      text.append(name, value); label.append(input, text); container.append(label);
    });
  }

  function applyTheme(theme) {
    selectedTheme = normalizeTheme(theme);
    document.body.classList.toggle('theme-light', selectedTheme === 'Light');
    document.body.classList.toggle('theme-luoxi', selectedTheme === 'Luoxi');
    const image = selectedTheme === 'Atri' ? "url('assets/Atri.jpg')" : selectedTheme === 'Luoxi' ? "url('assets/Luoxi.jpg')" : 'none';
    document.documentElement.style.setProperty('--theme-image', image);
    currentPalette = loadThemePalette(selectedTheme);
    applyPalette();
    $$('.theme-option').forEach(button => button.classList.toggle('active', button.dataset.theme === selectedTheme));
  }
  function hexToRgba(hex, opacity) {
    const value = String(hex || '#f5f9f4').replace('#', '');
    const safe = /^[0-9a-f]{6}$/i.test(value) ? value : 'f5f9f4';
    const red = parseInt(safe.slice(0, 2), 16), green = parseInt(safe.slice(2, 4), 16), blue = parseInt(safe.slice(4, 6), 16);
    return 'rgba(' + red + ',' + green + ',' + blue + ',' + opacity + ')';
  }

  function applyBackdrop() {
    document.documentElement.style.setProperty('--backdrop-blur', blurValue + 'px');
    document.documentElement.style.setProperty('--backdrop-opacity', (imageValue / 100).toFixed(2));
    const washOpacity = (washValue / 100).toFixed(2);
    const wash = $('.backdrop-wash');
    wash.style.setProperty('--wash-opacity', washOpacity);
    wash.style.backgroundColor = hexToRgba(currentPalette.wash || DEFAULT_PALETTES.Atri.wash, Number(washOpacity));
    $('#blurOutput').value = blurValue + ' px'; $('#washOutput').value = washValue + '%'; $('#imageOutput').value = imageValue + '%';
    $('#blurOutput').textContent = blurValue + ' px'; $('#washOutput').textContent = washValue + '%'; $('#imageOutput').textContent = imageValue + '%';
    localStorage.setItem('ocd-backdrop', JSON.stringify({ blur: blurValue, wash: washValue, image: imageValue }));
  }
  async function persistTheme(theme) {
    const normalized = theme === 'Luoxi' ? 'Luoxi' : theme === 'Light' ? 'Light' : 'Atri';
    applyTheme(normalized);
    try {
      await deps.bridgeCall('setTheme', { theme: normalized });
      deps.showToast('主题已应用并保存。');
    } catch (error) { deps.reportError(error); }
  }
      function initializeBackdrop() {
        try {
          const saved = JSON.parse(localStorage.getItem('ocd-backdrop') || '{}');
          blurValue = Number.isFinite(saved.blur) ? saved.blur : 2;
          washValue = Number.isFinite(saved.wash) ? saved.wash : 25;
          imageValue = Number.isFinite(saved.image) ? saved.image : 90;
        } catch (_) { blurValue = 2; washValue = 25; imageValue = 90; }
        $('#blurRange').value = blurValue; $('#washRange').value = washValue; $('#imageRange').value = imageValue;
        applyBackdrop();
      }
      function resetPalette() {
        localStorage.removeItem('ocd-colors-' + selectedTheme);
        applyTheme(selectedTheme);
      }
      function setBackdropValue(kind, value) {
        const number = Number(value);
        if (!Number.isFinite(number)) return;
        if (kind === 'blur') blurValue = number;
        else if (kind === 'wash') washValue = number;
        else if (kind === 'image') imageValue = number;
        applyBackdrop();
      }
      function resetBackdrop() {
        blurValue = 2; washValue = 25; imageValue = 90;
        $('#blurRange').value = blurValue; $('#washRange').value = washValue; $('#imageRange').value = imageValue;
        applyBackdrop();
      }
      return { applyTheme, persistTheme, initializeBackdrop, applyBackdrop, resetPalette, setBackdropValue, resetBackdrop };
    }
  };
})();
