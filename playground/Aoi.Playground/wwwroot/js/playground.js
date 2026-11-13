window.aoiPlayground = {
  getStoredTheme() {
    try { return localStorage.getItem('aoi-theme'); } catch { return null; }
  },
  setStoredTheme(theme) {
    try { localStorage.setItem('aoi-theme', theme); } catch { /* ignore */ }
  },
  prefersDark() {
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
  },
  applyTheme(theme) {
    document.documentElement.setAttribute('data-theme', theme);
  },

  _editors: new WeakMap(),

  bindEditor(textarea, highlight, gutter, activeLineEl, dotnetRef) {
    if (!textarea) return;
    const state = { highlight, gutter, activeLineEl, dotnetRef };
    this._editors.set(textarea, state);
    const onScroll = () => this.syncEditorScroll(textarea, highlight, gutter, activeLineEl);
    textarea.addEventListener('scroll', onScroll);
    state.onScroll = onScroll;
  },

  unbindEditor(textarea) {
    const state = this._editors.get(textarea);
    if (!state) return;
    if (state.onScroll) textarea.removeEventListener('scroll', state.onScroll);
    this._editors.delete(textarea);
  },

  getCursorLine(textarea) {
    if (!textarea) return 1;
    const value = textarea.value || '';
    const pos = Math.min(textarea.selectionStart ?? 0, value.length);
    let line = 1;
    for (let i = 0; i < pos; i++) {
      if (value.charCodeAt(i) === 10) line++;
    }
    return line;
  },

  lineHeightPx(textarea) {
    const style = window.getComputedStyle(textarea);
    const lh = parseFloat(style.lineHeight);
    if (!Number.isNaN(lh) && lh > 0) return lh;
    const fs = parseFloat(style.fontSize) || 14;
    return fs * 1.55;
  },

  updateActiveLine(textarea, activeLineEl, line) {
    if (!textarea || !activeLineEl) return;
    const lh = this.lineHeightPx(textarea);
    const top = (Math.max(1, line) - 1) * lh - textarea.scrollTop;
    activeLineEl.style.height = lh + 'px';
    activeLineEl.style.transform = 'translateY(' + top + 'px)';
  },

  syncEditorScroll(textarea, highlight, gutter, activeLineEl, line) {
    if (!textarea) return;
    if (highlight) {
      highlight.scrollTop = textarea.scrollTop;
      highlight.scrollLeft = textarea.scrollLeft;
    }
    if (gutter) gutter.scrollTop = textarea.scrollTop;
    if (activeLineEl && line != null) this.updateActiveLine(textarea, activeLineEl, line);
    else if (activeLineEl) this.updateActiveLine(textarea, activeLineEl, this.getCursorLine(textarea));
  },

  getStoredSplit() {
    try { return localStorage.getItem('aoi-split'); } catch { return null; }
  },
  setStoredSplit(value) {
    try { localStorage.setItem('aoi-split', value); } catch { /* ignore */ }
  },

  initVerticalSplit(stage, handle, key) {
    if (!stage || !handle) return () => {};
    const storageKey = key || 'aoi-split';
    const apply = (ratio) => {
      const r = Math.min(0.82, Math.max(0.28, ratio));
      stage.style.gridTemplateRows = `minmax(160px, ${r}fr) 10px minmax(120px, ${1 - r}fr)`;
      try { localStorage.setItem(storageKey, String(r)); } catch { /* ignore */ }
      return r;
    };

    let ratio = 0.58;
    try {
      const stored = localStorage.getItem(storageKey);
      if (stored) ratio = parseFloat(stored) || ratio;
    } catch { /* ignore */ }
    apply(ratio);

    let dragging = false;
    const onMove = (ev) => {
      if (!dragging) return;
      const rect = stage.getBoundingClientRect();
      if (rect.height <= 0) return;
      const y = (ev.touches ? ev.touches[0].clientY : ev.clientY) - rect.top;
      apply(y / rect.height);
      ev.preventDefault();
    };
    const onUp = () => {
      dragging = false;
      document.body.classList.remove('is-resizing');
    };
    const onDown = (ev) => {
      dragging = true;
      document.body.classList.add('is-resizing');
      onMove(ev);
      ev.preventDefault();
    };

    handle.addEventListener('mousedown', onDown);
    handle.addEventListener('touchstart', onDown, { passive: false });
    window.addEventListener('mousemove', onMove);
    window.addEventListener('touchmove', onMove, { passive: false });
    window.addEventListener('mouseup', onUp);
    window.addEventListener('touchend', onUp);

    return () => {
      handle.removeEventListener('mousedown', onDown);
      handle.removeEventListener('touchstart', onDown);
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('touchmove', onMove);
      window.removeEventListener('mouseup', onUp);
      window.removeEventListener('touchend', onUp);
    };
  },
};
