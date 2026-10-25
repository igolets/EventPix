// EventPix core UI logic. Contains NO markup and NO styling:
// all HTML comes from the active theme's templates.html, all looks from its CSS.
// Contract: docs/architecture.md#theming-contract. Always use textContent, never innerHTML, for data.

const token = location.pathname.split('/')[2] || '';
const base = `/api/e/${encodeURIComponent(token)}`;
const root = document.getElementById('app');
const PAGE_SIZE = 120;
const UPLOAD_CONCURRENCY = 3;
const state = {
  tabs: [], tab: null, items: [], lightboxIndex: -1,
  complete: false, loading: false,
  selecting: false, selected: new Set(),
};
let templates;
let strings = {};

// ---- i18n: UI strings come from the theme's "strings" JSON template; unknown keys fall back to English.
const DEFAULT_STRINGS = {
  rateLimited: 'Too many attempts. Please wait a few minutes.',
  emailInvalid: 'Please check the e-mail address.',
  codeInvalid: 'That code is not valid or has expired.',
  eyebrow: 'You are invited',
  emailLabel: 'Your e-mail',
  sendCode: 'Send me a code',
  codeLabel: 'Enter the 6-digit code we e-mailed you',
  viewAlbum: 'View the album',
  upload: 'Add photos & videos',
  notifyMe: 'Notify me',
  signout: 'Sign out',
  downloadTab: 'Download this tab',
  downloadStarred: 'Download starred ★',
  downloadAll: 'Download everything',
  empty: 'Nothing here yet. Be the first to add something!',
  notFoundTitle: 'Nothing here',
  notFoundText: 'This link is not valid.',
  uploading: 'Uploading… {percent}%',
  uploaded: 'Uploaded {count} file(s). Preparing previews…',
  uploadFailed: 'Upload failed: {name}',
  subscribeOk: 'You will get an e-mail when new files are added.',
  subscribeFail: 'Could not subscribe.',
  select: 'Select',
  selecting: 'Cancel selection',
  downloadSelected: 'Download selected ({count})',
  downloadFailed: 'Could not download the selection.',
  unsubTitle: 'Unsubscribe',
  unsubOk: 'You have been unsubscribed. You will not receive any more notifications for this album.',
  unsubInvalid: 'This link is not valid or you are already unsubscribed.',
  unsubBadLink: 'This link is not valid.',
  ariaOpen: 'Open',
  ariaStar: 'Star',
  ariaClose: 'Close',
  ariaPrev: 'Previous',
  ariaNext: 'Next',
  ariaFullscreen: 'Full screen',
  ariaDownload: 'Download',
};

const t = (key, params) => {
  let s = strings[key] ?? DEFAULT_STRINGS[key] ?? key;
  for (const [k, v] of Object.entries(params ?? {})) s = s.split(`{${k}}`).join(String(v));
  return s;
};

// Theme markup declares translatable text with data-i18n="key" (textContent) and
// data-i18n-aria="key" (aria-label). The core fills both from the strings system.
function applyI18n(scope) {
  for (const el of scope.querySelectorAll('[data-i18n]')) el.textContent = t(el.dataset.i18n);
  for (const el of scope.querySelectorAll('[data-i18n-aria]')) el.setAttribute('aria-label', t(el.dataset.i18nAria));
}

const api = (path, opts = {}) =>
  fetch(base + path, { credentials: 'same-origin', ...opts, headers: { 'X-Requested-With': 'eventpix', ...(opts.headers || {}) } });
const jsonBody = (obj) => ({ body: JSON.stringify(obj), headers: { 'Content-Type': 'application/json' } });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function tpl(name) {
  const t2 = templates.querySelector(`template[data-tpl="${name}"]`);
  if (!t2) throw new Error(`Theme is missing <template data-tpl="${name}">`);
  const el = document.importNode(t2.content, true).firstElementChild;
  el.dataset.tpl = name;   // view marker: state hooks for CSS and tests
  applyI18n(el);
  return el;
}
const ref = (el, name) => el.querySelector(`[data-ref="${name}"]`);
const text = (el, name, value) => { const n = ref(el, name); if (n) n.textContent = value; };
const mount = (el) => root.replaceChildren(el);

// ---- login -------------------------------------------------------------------------------------------
function showLogin(info) {
  const view = tpl('login');
  text(view, 'event-name', info.name);
  text(view, 'event-description', info.description || '');
  const emailForm = ref(view, 'email-form'), codeForm = ref(view, 'code-form'), msg = ref(view, 'message');
  codeForm.hidden = true;
  let email = '';

  emailForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    email = ref(view, 'email').value.trim();
    msg.textContent = '';
    const res = await api('/auth/request', { method: 'POST', ...jsonBody({ email }) });
    if (res.ok) { emailForm.hidden = true; codeForm.hidden = false; ref(view, 'code').focus(); }
    else msg.textContent = res.status === 429 ? t('rateLimited') : t('emailInvalid');
  });
  codeForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    const res = await api('/auth/verify', { method: 'POST', ...jsonBody({ email, code: ref(view, 'code').value }) });
    if (res.ok) return start();
    msg.textContent = t('codeInvalid');
  });
  mount(view);
}

// ---- gallery -------------------------------------------------------------------------------------------
async function showGallery(info) {
  const view = tpl('gallery');
  text(view, 'event-name', info.name);
  ref(view, 'download-all').href = `${base}/download`;
  ref(view, 'download-starred').href = `${base}/download?starred=true`;
  ref(view, 'signout').addEventListener('click', async () => { await api('/auth/logout', { method: 'POST' }); location.reload(); });
  ref(view, 'upload-input').addEventListener('change', (e) => upload(view, [...e.target.files], e.target));
  ref(view, 'subscribe').addEventListener('click', async () => {
    const res = await api('/subscriptions', { method: 'PUT', ...jsonBody({ channel: 'Email', tabId: null, mode: 'Instant' }) });
    text(view, 'status', res.ok ? t('subscribeOk') : t('subscribeFail'));
  });

  const selectBtn = ref(view, 'select');
  selectBtn.addEventListener('click', () => setSelecting(view, !state.selecting));
  const dlSelected = ref(view, 'download-selected');
  dlSelected.hidden = true;
  dlSelected.addEventListener('click', () => downloadSelected(view));

  mount(view);
  state.view = view;
  watchSentinel(view);
  await loadTabs();
}

function watchSentinel(view) {
  const sentinel = ref(view, 'sentinel');
  if (!sentinel) return;   // themes without a sentinel get the first page only
  new IntersectionObserver((entries) => {
    if (entries.some((e) => e.isIntersecting)) loadMore();
  }, { rootMargin: '600px 0px' }).observe(sentinel);
}

async function loadTabs(selectId) {
  state.tabs = await (await api('/tabs')).json();
  const view = state.view, bar = ref(view, 'tabs');
  bar.replaceChildren();
  for (const t2 of state.tabs) {
    const b = tpl('tab');
    text(b, 'label', t2.name);
    text(b, 'count', String(t2.count));
    b.dataset.kind = t2.kind.toLowerCase();
    b.addEventListener('click', () => selectTab(t2.id));
    b.dataset.tabId = t2.id;
    bar.append(b);
  }
  const pick = selectId ?? state.tab?.id ?? state.tabs[0]?.id;
  if (pick) await selectTab(pick); else renderGrid();
}

async function selectTab(id) {
  state.tab = state.tabs.find((t2) => t2.id === id) ?? null;
  for (const b of ref(state.view, 'tabs').children) b.setAttribute('aria-selected', String(b.dataset.tabId === id));
  ref(state.view, 'download-tab').href = `${base}/download?tab=${id}`;
  state.items = [];
  state.complete = false;
  ref(state.view, 'grid').replaceChildren();
  await loadMore();
}

async function loadMore() {
  if (!state.tab || state.complete || state.loading) return;
  state.loading = true;
  try {
    const items = await (await api(`/tabs/${state.tab.id}/media?skip=${state.items.length}&take=${PAGE_SIZE}`)).json();
    const grid = ref(state.view, 'grid');
    ref(state.view, 'empty').hidden = state.items.length + items.length > 0;
    for (const m of items) grid.append(makeTile(m, state.items.length));
    state.items.push(...items);
    state.complete = items.length < PAGE_SIZE;
    grid.dataset.complete = String(state.complete);
  } finally {
    state.loading = false;
  }
  fillVisible();
}

// Keep loading while the sentinel is (still) on screen, e.g. on tall viewports.
function fillVisible() {
  const sentinel = ref(state.view, 'sentinel');
  if (!sentinel || state.complete) return;
  if (sentinel.getBoundingClientRect().top < innerHeight + 600) queueMicrotask(loadMore);
}

function makeTile(m, index) {
  const tile = tpl('tile');
  tile.dataset.kind = m.kind.toLowerCase();
  tile.dataset.starred = String(m.starred);
  tile.dataset.selected = String(state.selected.has(m.id));
  if (m.width && m.height) tile.style.setProperty('--ratio', `${m.width} / ${m.height}`);
  const img = ref(tile, 'image');
  img.src = m.status === 'Ready' ? `${base}/media/${m.id}/thumb` : '';
  img.alt = m.name;
  ref(tile, 'open').addEventListener('click', () => (state.selecting ? toggleSelect(m, tile) : openLightbox(index)));
  ref(tile, 'star').addEventListener('click', () => toggleStar(m, tile));
  return tile;
}

function renderGrid() {
  const grid = ref(state.view, 'grid');
  grid.replaceChildren();
  ref(state.view, 'empty').hidden = state.items.length > 0;
  state.items.forEach((m, i) => grid.append(makeTile(m, i)));
}

async function toggleStar(m, tile) {
  const res = await api(`/media/${m.id}/star`, { method: m.starred ? 'DELETE' : 'PUT' });
  if (res.ok) { m.starred = !m.starred; tile.dataset.starred = String(m.starred); }
}

// ---- uploads: parallel transfers with aggregate progress, then auto-refresh while processing -----------
async function upload(view, files, input) {
  if (files.length === 0) return;
  const status = ref(view, 'status');
  const bar = ref(view, 'progress');
  view.dataset.uploading = 'true';
  const q = state.tab?.canUpload ? `?tab=${state.tab.id}` : '';
  const totalBytes = files.reduce((s, f) => s + f.size, 0);
  const loaded = new Map();
  let finished = 0;

  const render = () => {
    const bytes = [...loaded.values()].reduce((s, n) => s + n, 0);
    const percent = totalBytes ? Math.round((bytes / totalBytes) * 100) : 0;
    if (bar) bar.style.setProperty('--progress', `${percent}%`);
    status.textContent = t('uploading', { percent });
  };

  render();
  const sendOne = (file) => new Promise((resolve) => {
    const fd = new FormData(); fd.append('files', file);
    const xhr = new XMLHttpRequest();
    xhr.open('POST', `${base}/uploads${q}`);
    xhr.responseType = 'json';
    xhr.setRequestHeader('X-Requested-With', 'eventpix');
    xhr.upload.onprogress = (e) => { loaded.set(file, e.loaded); render(); };
    xhr.onload = () => { loaded.set(file, file.size); finished += 1; render(); resolve(xhr.status >= 200 && xhr.status < 300 ? null : file.name); };
    xhr.onerror = () => resolve(file.name);
    xhr.send(fd);
  });

  const queue = [...files];
  const failures = await Promise.all(Array.from({ length: Math.min(UPLOAD_CONCURRENCY, queue.length) }, async () => {
    let failure = null;
    for (;;) {
      const file = queue.shift();
      if (!file) return failure;
      const name = await sendOne(file);
      if (name && !failure) failure = name;
    }
  }));
  const failed = failures.find(Boolean) ?? null;

  view.dataset.uploading = 'false';
  input.value = '';
  const tabId = state.tab?.id;
  status.textContent = failed ? t('uploadFailed', { name: failed }) : t('uploaded', { count: finished });
  await refreshUntilReady(tabId);
  await loadTabs(tabId);
}

// Poll the tab until the worker has produced every thumbnail (or the user moves on).
async function refreshUntilReady(tabId) {
  for (let i = 0; i < 30; i++) {
    await sleep(1500);
    if (!tabId || state.tab?.id !== tabId || state.view?.dataset.uploading === 'true') return;
    await reloadCurrentTab();
    if (!state.items.some((m) => m.status !== 'Ready')) return;
  }
}

async function reloadCurrentTab() {
  if (!state.tab) return;
  const items = await (await api(`/tabs/${state.tab.id}/media?take=${PAGE_SIZE}`)).json();
  state.items = items;
  state.complete = items.length < PAGE_SIZE;
  const grid = ref(state.view, 'grid');
  grid.dataset.complete = String(state.complete);
  renderGrid();
}

// ---- selection mode: multi-select tiles, download as one ZIP -------------------------------------------
function setSelecting(view, on) {
  state.selecting = on;
  if (!on) {
    state.selected.clear();
    for (const tile of ref(view, 'grid').children) tile.dataset.selected = 'false';
  }

  view.dataset.selecting = String(on);
  const btn = ref(view, 'select');
  btn.textContent = on ? t('selecting') : t('select');
  btn.setAttribute('aria-pressed', String(on));
  updateDownloadSelected(view);
}

function toggleSelect(m, tile) {
  if (state.selected.has(m.id)) state.selected.delete(m.id); else state.selected.add(m.id);
  tile.dataset.selected = String(state.selected.has(m.id));
  updateDownloadSelected(state.view);
}

function updateDownloadSelected(view) {
  const btn = ref(view, 'download-selected');
  btn.hidden = state.selected.size === 0;
  btn.textContent = t('downloadSelected', { count: state.selected.size });
}

async function downloadSelected(view) {
  const status = ref(view, 'status');
  const res = await api('/download', { method: 'POST', ...jsonBody({ ids: [...state.selected] }) });
  if (!res.ok) { status.textContent = t('downloadFailed'); return; }
  const url = URL.createObjectURL(await res.blob());
  const a = document.createElement('a');
  a.href = url;
  a.download = 'eventpix-selection.zip';
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 30_000);
}

// ---- lightbox with zoom (wheel, double click, pinch) ------------------------------------------------------
function openLightbox(index) {
  const box = tpl('lightbox');
  const stage = ref(box, 'stage');
  const zoom = createZoom(box, stage);
  const show = (i) => {
    state.lightboxIndex = (i + state.items.length) % state.items.length;
    const m = state.items[state.lightboxIndex];
    stage.replaceChildren();
    let el;
    if (m.kind === 'Video') { el = document.createElement('video'); el.controls = true; el.autoplay = true; el.playsInline = true; el.src = `${base}/media/${m.id}/original`; }
    else { el = document.createElement('img'); el.src = `${base}/media/${m.id}/preview`; el.alt = m.name; }
    stage.append(el);
    zoom.reset();
    text(box, 'caption', `${m.uploader} · ${m.name}`);
    ref(box, 'download').href = `${base}/media/${m.id}/original?download=true`;
    box.dataset.starred = String(m.starred);
  };
  const close = () => { box.remove(); document.removeEventListener('keydown', onKey); if (document.fullscreenElement) document.exitFullscreen(); };
  const onKey = (e) => { if (e.key === 'Escape') close(); if (e.key === 'ArrowRight') show(state.lightboxIndex + 1); if (e.key === 'ArrowLeft') show(state.lightboxIndex - 1); };
  ref(box, 'close').addEventListener('click', close);
  ref(box, 'prev').addEventListener('click', () => show(state.lightboxIndex - 1));
  ref(box, 'next').addEventListener('click', () => show(state.lightboxIndex + 1));
  ref(box, 'fullscreen').addEventListener('click', () => document.fullscreenElement ? document.exitFullscreen() : box.requestFullscreen?.());
  ref(box, 'star').addEventListener('click', async () => {
    const m = state.items[state.lightboxIndex];
    const res = await api(`/media/${m.id}/star`, { method: m.starred ? 'DELETE' : 'PUT' });
    if (res.ok) { m.starred = !m.starred; box.dataset.starred = String(m.starred); renderGrid(); }
  });
  document.addEventListener('keydown', onKey);
  document.body.append(box);
  show(index);
}

// Pointer-events pinch + wheel + double click. All state stays in JS; the theme styles [data-zoom].
function createZoom(box, stage) {
  let scale = 1, tx = 0, ty = 0, pinch = null, last = null;
  const pointers = new Map();
  const apply = () => {
    const el = stage.firstElementChild;
    if (el) el.style.transform = `translate(${tx}px, ${ty}px) scale(${scale})`;
    box.dataset.zoom = String(scale > 1.01);
  };
  const reset = () => { scale = 1; tx = 0; ty = 0; apply(); };
  const zoomBy = (factor, cx = 0, cy = 0) => {
    const next = Math.min(8, Math.max(1, scale * factor));
    const k = next / scale;
    tx = (tx - cx) * k + cx;
    ty = (ty - cy) * k + cy;
    scale = next;
    if (scale === 1) { tx = 0; ty = 0; }
    apply();
  };
  const midOf = () => {
    const pts = [...pointers.values()];
    const rect = stage.getBoundingClientRect();
    return {
      dist: Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y) || 1,
      x: (pts[0].x + pts[1].x) / 2 - rect.left - rect.width / 2,
      y: (pts[0].y + pts[1].y) / 2 - rect.top - rect.height / 2,
    };
  };

  stage.addEventListener('wheel', (e) => {
    e.preventDefault();
    const rect = stage.getBoundingClientRect();
    zoomBy(Math.exp(-e.deltaY * 0.0015), e.clientX - rect.left - rect.width / 2, e.clientY - rect.top - rect.height / 2);
  }, { passive: false });

  stage.addEventListener('pointerdown', (e) => {
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    stage.setPointerCapture(e.pointerId);
    if (pointers.size === 2) { const m = midOf(); pinch = { dist: m.dist, x: m.x, y: m.y, scale, tx, ty }; }
    last = { x: e.clientX, y: e.clientY };
  });
  stage.addEventListener('pointermove', (e) => {
    if (!pointers.has(e.pointerId)) return;
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (pointers.size === 2 && pinch) {
      const m = midOf();
      const k = (pinch.scale * (m.dist / pinch.dist)) / scale;
      tx = (tx - m.x) * k + m.x;
      ty = (ty - m.y) * k + m.y;
      scale *= k;
      tx += m.x - pinch.x;
      ty += m.y - pinch.y;
      apply();
    } else if (scale > 1 && last) {
      tx += e.clientX - last.x;
      ty += e.clientY - last.y;
      apply();
    }

    last = { x: e.clientX, y: e.clientY };
  });
  const release = (e) => {
    pointers.delete(e.pointerId);
    if (pointers.size < 2) pinch = null;
    last = pointers.size ? [...pointers.values()][0] : null;
  };
  stage.addEventListener('pointerup', release);
  stage.addEventListener('pointercancel', release);
  stage.addEventListener('dblclick', () => (scale > 1.01 ? reset() : zoomBy(2.5)));

  return { reset };
}

// ---- unsubscribe --------------------------------------------------------------------------------------------
// One-click unsubscribe page: /u/{subscriptionId}.{hmac}. Confirms by POSTing to the API, then shows the result.
async function showUnsubscribe() {
  const view = tpl('unsubscribe');
  const message = ref(view, 'message');
  text(view, 'title', t('unsubTitle'));
  mount(view);

  const seg = location.pathname.split('/')[2] || '';
  const dot = seg.indexOf('.');
  if (dot <= 0) { message.textContent = t('unsubBadLink'); return; }

  const res = await fetch(`/api/unsubscribe/${encodeURIComponent(seg.slice(0, dot))}/${encodeURIComponent(seg.slice(dot + 1))}`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'X-Requested-With': 'eventpix' },
  });
  message.textContent = res.ok ? t('unsubOk') : t('unsubInvalid');
}

// ---- boot ------------------------------------------------------------------------------------------------------
async function start() {
  if (location.pathname.startsWith('/u/')) return showUnsubscribe();
  const pub = await api('/public');
  if (!pub.ok) { mount(tpl('not-found')); return; }          // same page for "wrong link" as for anything else
  const info = await pub.json();
  const me = await api('/me');
  return me.ok ? showGallery(info) : showLogin(info);
}

const html = await (await fetch('/theme/templates.html')).text();
templates = new DOMParser().parseFromString(html, 'text/html');
loadStrings();
start();

// Base strings come from <template data-tpl="strings"> (English fallback). A theme may ship translations as
// additional <template data-tpl="strings" data-lang="ru"> variants; the ?lang= query parameter overrides
// the browser language. Matching is exact tag first, then primary subtag ("ru-RU" picks "ru").
function loadStrings() {
  const parse = (el) => {
    try { return el ? JSON.parse(el.content.textContent) ?? {} : null; } catch { return null; }
  };

  strings = parse(templates.querySelector('template[data-tpl="strings"]:not([data-lang])')) ?? {};
  const wanted = (new URLSearchParams(location.search).get('lang') ?? navigator.language ?? '').toLowerCase();
  if (!wanted) return;

  const available = [...templates.querySelectorAll('template[data-tpl="strings"][data-lang]')]
    .map((el) => (el.getAttribute('data-lang') ?? '').toLowerCase());
  const primary = wanted.split('-')[0];
  const lang = available.find((l) => l === wanted) ?? available.find((l) => l.split('-')[0] === primary);
  if (!lang) return;

  const overlay = parse(templates.querySelector(`template[data-tpl="strings"][data-lang="${lang}"]`));
  if (overlay) strings = { ...strings, ...overlay };
}
