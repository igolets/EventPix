// EventPix core UI logic. Contains NO markup and NO styling:
// all HTML comes from the active theme's templates.html, all looks from its CSS.
// Contract: docs/architecture.md#theming-contract. Always use textContent, never innerHTML, for data.

const token = location.pathname.split('/')[2] || '';
const base = `/api/e/${encodeURIComponent(token)}`;
const root = document.getElementById('app');
const state = { tabs: [], tab: null, items: [], lightboxIndex: -1 };
let templates;

const api = (path, opts = {}) =>
  fetch(base + path, { credentials: 'same-origin', ...opts, headers: { 'X-Requested-With': 'eventpix', ...(opts.headers || {}) } });
const jsonBody = (obj) => ({ body: JSON.stringify(obj), headers: { 'Content-Type': 'application/json' } });

function tpl(name) {
  const t = templates.querySelector(`template[data-tpl="${name}"]`);
  if (!t) throw new Error(`Theme is missing <template data-tpl="${name}">`);
  return document.importNode(t.content, true).firstElementChild;
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
    else msg.textContent = res.status === 429 ? 'Too many attempts. Please wait a few minutes.' : 'Please check the e-mail address.';
  });
  codeForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    const res = await api('/auth/verify', { method: 'POST', ...jsonBody({ email, code: ref(view, 'code').value }) });
    if (res.ok) return start();
    msg.textContent = 'That code is not valid or has expired.';
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
    text(view, 'status', res.ok ? 'You will get an e-mail when new files are added.' : 'Could not subscribe.');
  });
  mount(view);
  state.view = view;
  await loadTabs();
}

async function loadTabs(selectId) {
  state.tabs = await (await api('/tabs')).json();
  const view = state.view, bar = ref(view, 'tabs');
  bar.replaceChildren();
  for (const t of state.tabs) {
    const b = tpl('tab');
    text(b, 'label', t.name);
    text(b, 'count', String(t.count));
    b.dataset.kind = t.kind.toLowerCase();
    b.addEventListener('click', () => selectTab(t.id));
    b.dataset.tabId = t.id;
    bar.append(b);
  }
  const pick = selectId ?? state.tab?.id ?? state.tabs[0]?.id;
  if (pick) await selectTab(pick); else renderGrid();
}

async function selectTab(id) {
  state.tab = state.tabs.find((t) => t.id === id) ?? null;
  for (const b of ref(state.view, 'tabs').children) b.setAttribute('aria-selected', String(b.dataset.tabId === id));
  ref(state.view, 'download-tab').href = `${base}/download?tab=${id}`;
  state.items = await (await api(`/tabs/${id}/media`)).json();
  renderGrid();
}

function renderGrid() {
  const grid = ref(state.view, 'grid');
  grid.replaceChildren();
  ref(state.view, 'empty').hidden = state.items.length > 0;
  state.items.forEach((m, i) => {
    const tile = tpl('tile');
    tile.dataset.kind = m.kind.toLowerCase();
    tile.dataset.starred = String(m.starred);
    if (m.width && m.height) tile.style.setProperty('--ratio', `${m.width} / ${m.height}`);
    const img = ref(tile, 'image');
    img.src = m.status === 'Ready' ? `${base}/media/${m.id}/thumb` : '';
    img.alt = m.name;
    ref(tile, 'open').addEventListener('click', () => openLightbox(i));
    ref(tile, 'star').addEventListener('click', () => toggleStar(m, tile));
    grid.append(tile);
  });
}

async function toggleStar(m, tile) {
  const res = await api(`/media/${m.id}/star`, { method: m.starred ? 'DELETE' : 'PUT' });
  if (res.ok) { m.starred = !m.starred; tile.dataset.starred = String(m.starred); }
}

async function upload(view, files, input) {
  const status = ref(view, 'status');
  let done = 0;
  for (const f of files) {
    status.textContent = `Uploading ${done + 1} of ${files.length}: ${f.name}`;
    const fd = new FormData(); fd.append('files', f);
    const q = state.tab?.canUpload ? `?tab=${state.tab.id}` : '';
    const res = await api(`/uploads${q}`, { method: 'POST', body: fd });
    if (!res.ok) { status.textContent = `Failed: ${f.name}`; break; }
    done++;
  }
  input.value = '';
  if (done === files.length) status.textContent = `Uploaded ${done} file(s). Thumbnails may take a moment.`;
  await loadTabs(state.tab?.id);
}

// ---- lightbox ----------------------------------------------------------------------------------------------
function openLightbox(index) {
  const box = tpl('lightbox');
  const stage = ref(box, 'stage');
  const show = (i) => {
    state.lightboxIndex = (i + state.items.length) % state.items.length;
    const m = state.items[state.lightboxIndex];
    stage.replaceChildren();
    let el;
    if (m.kind === 'Video') { el = document.createElement('video'); el.controls = true; el.autoplay = true; el.playsInline = true; el.src = `${base}/media/${m.id}/original`; }
    else { el = document.createElement('img'); el.src = `${base}/media/${m.id}/preview`; el.alt = m.name; }
    stage.append(el);
    text(box, 'caption', `${m.uploader} · ${m.name}`);
    ref(box, 'download').href = `${base}/media/${m.id}/original?download=true`;
    box.dataset.starred = String(m.starred);
  };
  const close = () => {
    box.remove();
    document.removeEventListener('keydown', onKey);
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  };
  const onKey = (e) => { if (e.key === 'Escape') close(); if (e.key === 'ArrowRight') show(state.lightboxIndex + 1); if (e.key === 'ArrowLeft') show(state.lightboxIndex - 1); };
  ref(box, 'close').addEventListener('click', close);
  ref(box, 'prev').addEventListener('click', () => show(state.lightboxIndex - 1));
  ref(box, 'next').addEventListener('click', () => show(state.lightboxIndex + 1));
  ref(box, 'fullscreen').addEventListener('click', () => {
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    else box.requestFullscreen?.().catch(() => {});
  });
  ref(box, 'star').addEventListener('click', async () => {
    const m = state.items[state.lightboxIndex];
    const res = await api(`/media/${m.id}/star`, { method: m.starred ? 'DELETE' : 'PUT' });
    if (res.ok) { m.starred = !m.starred; box.dataset.starred = String(m.starred); renderGrid(); }
  });
  document.addEventListener('keydown', onKey);
  document.body.append(box);
  show(index);
}

// ---- unsubscribe --------------------------------------------------------------------------------------------
// One-click unsubscribe page: /u/{subscriptionId}.{hmac}. Confirms by POSTing to the API, then shows the result.
async function showUnsubscribe() {
  const view = tpl('unsubscribe');
  const message = ref(view, 'message');
  text(view, 'title', 'Unsubscribe');
  mount(view);

  const seg = location.pathname.split('/')[2] || '';
  const dot = seg.indexOf('.');
  if (dot <= 0) { message.textContent = 'This link is not valid.'; return; }

  const res = await fetch(`/api/unsubscribe/${encodeURIComponent(seg.slice(0, dot))}/${encodeURIComponent(seg.slice(dot + 1))}`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'X-Requested-With': 'eventpix' },
  });
  message.textContent = res.ok
    ? 'You have been unsubscribed. You will not receive any more notifications for this album.'
    : 'This link is not valid or you are already unsubscribed.';
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
start();
