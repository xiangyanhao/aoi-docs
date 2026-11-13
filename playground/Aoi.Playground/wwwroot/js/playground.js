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
  syncEditorScroll(textarea, highlight) {
    if (!textarea || !highlight) return;
    highlight.scrollTop = textarea.scrollTop;
    highlight.scrollLeft = textarea.scrollLeft;
  },
};
