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
