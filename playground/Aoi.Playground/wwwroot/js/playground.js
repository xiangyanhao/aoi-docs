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

  /** Keep the transparent textarea DOM value in sync after SPA remounts. */
  setEditorValue(textarea, value) {
    if (!textarea) return;
    const next = value ?? '';
    if (textarea.value !== next) textarea.value = next;
  },

  _audioHandlers: new WeakMap(),

  bindAudio(audio, dotnetRef) {
    if (!audio) return;
    this.unbindAudio(audio);
    const onEnded = () => dotnetRef.invokeMethodAsync('OnAudioEnded');
    const onPause = () => {
      if (!audio.ended) dotnetRef.invokeMethodAsync('OnAudioPause');
    };
    const onPlay = () => dotnetRef.invokeMethodAsync('OnAudioPlay');
    audio.addEventListener('ended', onEnded);
    audio.addEventListener('pause', onPause);
    audio.addEventListener('play', onPlay);
    this._audioHandlers.set(audio, { onEnded, onPause, onPlay });
  },

  unbindAudio(audio) {
    const h = this._audioHandlers.get(audio);
    if (!h || !audio) return;
    audio.removeEventListener('ended', h.onEnded);
    audio.removeEventListener('pause', h.onPause);
    audio.removeEventListener('play', h.onPlay);
    this._audioHandlers.delete(audio);
  },

  async playAudio(audio) {
    if (!audio) throw new Error('audio missing');
    await audio.play();
  },

  pauseAudio(audio) {
    if (!audio) return;
    audio.pause();
  },

  getAudioProgress(audio) {
    if (!audio || !audio.duration || !Number.isFinite(audio.duration) || audio.duration <= 0) return 0;
    return Math.min(1, Math.max(0, audio.currentTime / audio.duration));
  },

  seekAudioAtClientX(audio, clientX) {
    if (!audio || !audio.duration || !Number.isFinite(audio.duration)) return;
    const bar = audio.parentElement?.querySelector('.music-bar');
    if (!bar) return;
    const rect = bar.getBoundingClientRect();
    if (rect.width <= 0) return;
    const ratio = Math.min(1, Math.max(0, (clientX - rect.left) / rect.width));
    audio.currentTime = ratio * audio.duration;
  },

  _dragHandlers: new WeakMap(),

  /**
   * Make the music player draggable across the viewport.
   * Interactive controls (buttons / seek / links) are ignored as drag starts.
   * Position is persisted in localStorage under `key`.
   */
  initDraggable(el, key) {
    if (!el) return () => {};
    this.disposeDraggable(el);
    const storageKey = key || 'aoi-music-pos';
    const margin = 8;

    const clamp = (left, top) => {
      const w = el.offsetWidth || 330;
      const h = el.offsetHeight || 86;
      const maxL = Math.max(margin, window.innerWidth - w - margin);
      const maxT = Math.max(margin, window.innerHeight - h - margin);
      return {
        left: Math.min(maxL, Math.max(margin, left)),
        top: Math.min(maxT, Math.max(margin, top)),
      };
    };

    const apply = (left, top, persist) => {
      const p = clamp(left, top);
      el.style.left = p.left + 'px';
      el.style.top = p.top + 'px';
      el.style.right = 'auto';
      el.style.bottom = 'auto';
      el.classList.add('is-placed');
      if (persist) {
        try { localStorage.setItem(storageKey, JSON.stringify(p)); } catch { /* ignore */ }
      }
      return p;
    };

    try {
      const raw = localStorage.getItem(storageKey);
      if (raw) {
        const saved = JSON.parse(raw);
        if (saved && typeof saved.left === 'number' && typeof saved.top === 'number') {
          apply(saved.left, saved.top, false);
        }
      }
    } catch { /* ignore */ }

    let dragging = false;
    let moved = false;
    let startX = 0;
    let startY = 0;
    let origL = 0;
    let origT = 0;

    const point = (ev) => {
      const t = ev.touches ? ev.touches[0] : ev;
      return { x: t.clientX, y: t.clientY };
    };

    const onMove = (ev) => {
      if (!dragging) return;
      const p = point(ev);
      const dx = p.x - startX;
      const dy = p.y - startY;
      if (Math.abs(dx) > 3 || Math.abs(dy) > 3) moved = true;
      apply(origL + dx, origT + dy, false);
      ev.preventDefault();
    };

    const onUp = () => {
      if (!dragging) return;
      dragging = false;
      el.classList.remove('is-dragging');
      document.body.classList.remove('is-music-dragging');
      const rect = el.getBoundingClientRect();
      apply(rect.left, rect.top, true);
    };

    const onDown = (ev) => {
      if (ev.button != null && ev.button !== 0) return;
      if (ev.target.closest('button, a, .music-bar, input, textarea, select')) return;
      const p = point(ev);
      const rect = el.getBoundingClientRect();
      dragging = true;
      moved = false;
      startX = p.x;
      startY = p.y;
      origL = rect.left;
      origT = rect.top;
      el.classList.add('is-dragging');
      document.body.classList.add('is-music-dragging');
      // Switch from bottom anchoring to top/left on first drag.
      apply(rect.left, rect.top, false);
      ev.preventDefault();
    };

    const onClickCapture = (ev) => {
      // After a real drag, swallow the click so controls under the cursor do not fire.
      if (moved) {
        ev.preventDefault();
        ev.stopPropagation();
        moved = false;
      }
    };

    const onResize = () => {
      const rect = el.getBoundingClientRect();
      if (el.classList.contains('is-placed')) apply(rect.left, rect.top, true);
    };

    el.addEventListener('mousedown', onDown);
    el.addEventListener('touchstart', onDown, { passive: false });
    el.addEventListener('click', onClickCapture, true);
    window.addEventListener('mousemove', onMove);
    window.addEventListener('touchmove', onMove, { passive: false });
    window.addEventListener('mouseup', onUp);
    window.addEventListener('touchend', onUp);
    window.addEventListener('resize', onResize);

    const dispose = () => {
      el.removeEventListener('mousedown', onDown);
      el.removeEventListener('touchstart', onDown);
      el.removeEventListener('click', onClickCapture, true);
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('touchmove', onMove);
      window.removeEventListener('mouseup', onUp);
      window.removeEventListener('touchend', onUp);
      window.removeEventListener('resize', onResize);
      this._dragHandlers.delete(el);
    };
    this._dragHandlers.set(el, dispose);
    return dispose;
  },

  disposeDraggable(el) {
    const dispose = this._dragHandlers.get(el);
    if (dispose) dispose();
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
