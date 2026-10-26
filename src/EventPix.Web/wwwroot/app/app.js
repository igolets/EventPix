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
  isOwner: false,
  guest: false,          // browsing an open album without a session (view-only)
  pendingDialog: false,  // upload clicked as guest: open the file dialog right after the OTP round-trip
  info: null,
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
  subsTitle: 'Notifications',
  subsChannel: 'Channel',
  subsEmail: 'E-mail',
  subsTelegram: 'Telegram',
  subsTab: 'Album',
  subsAllTabs: 'All albums',
  subsMode: 'Frequency',
  subsInstant: 'Instantly',
  subsDigest: 'Daily digest',
  subsAdd: 'Save',
  subsEmpty: 'No notifications yet.',
  subsRemove: 'Remove',
  subsFail: 'Could not save the subscription.',
  subsTelegramHint: 'Open this link in Telegram to finish:',
  subsTelegramOff: 'Telegram notifications are not configured on this server.',
  select: 'Select',
  selecting: 'Cancel selection',
  downloadSelected: 'Download selected ({count})',
  downloadFailed: 'Could not download the selection.',
  unsubTitle: 'Unsubscribe',
  unsubOk: 'You have been unsubscribed. You will not receive any more notifications for this album.',
  unsubInvalid: 'This link is not valid or you are already unsubscribed.',
  unsubBadLink: 'This link is not valid.',
  tabNew: 'New tab',
  tabRename: 'Rename',
  tabCreateTitle: 'New tab',
  tabRenameTitle: 'Rename tab',
  tabNameLabel: 'Name',
  tabSave: 'Save',
  tabDelete: 'Delete tab',
  tabDeleteArmed: 'Really delete?',
  tabNameInUse: 'You already have a tab with that name.',
  tabFail: 'Could not save the tab.',
  manageTabs: 'Manage tabs',
  manageTitle: 'Manage tabs',
  addSection: 'Add section',
  guestUploads: 'Guests may upload',
  invites: 'Invites',
  invitesTitle: 'Invites',
  invitesEmailLabel: 'E-mail',
  invitesAdd: 'Invite',
  invitesEmpty: 'No invites yet.',
  invitesJoined: 'joined',
  invitesPending: 'pending',
  inviteFail: 'Could not save the invite.',
  mediaDeleteArmed: 'Really delete?',
  ariaOpen: 'Open',
  ariaStar: 'Star',
  ariaClose: 'Close',
  ariaPrev: 'Previous',
  ariaNext: 'Next',
  ariaFullscreen: 'Full screen',
  ariaDownload: 'Download',
  ariaRename: 'Rename tab',
  ariaDelete: 'Delete',
  ariaUp: 'Move up',
  ariaDown: 'Move down',
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
  state.info = info;
  text(view, 'event-name', info.name);
  ref(view, 'download-all').href = `${base}/download`;
  ref(view, 'download-starred').href = `${base}/download?starred=true`;
  if (state.guest) {
    ref(view, 'signout').hidden = true;   // the upload button asks for the OTP instead
    ref(view, 'subscribe').hidden = true;      // notifications need a proven e-mail
    ref(view, 'tab-add').hidden = true;        // personal tabs need a member
    ref(view, 'download-starred').hidden = true;
  } else {
    ref(view, 'signout').addEventListener('click', async () => { await api('/auth/logout', { method: 'POST' }); location.reload(); });
    ref(view, 'subscribe').addEventListener('click', () => openSubscriptions());
    ref(view, 'tab-add').addEventListener('click', () => openTabEditor());
  }
  const uploadInput = ref(view, 'upload-input');
  uploadInput.closest('label')?.addEventListener('click', (e) => {
    if (state.guest) {   // guests get the OTP prompt instead of the file dialog
      e.preventDefault();
      state.pendingDialog = true;
      showLogin(state.info);
    }
  });
  uploadInput.addEventListener('change', (e) => upload(view, [...e.target.files], e.target));
  ref(view, 'tab-rename').addEventListener('click', () => openTabEditor(state.tab));
  ref(view, 'manage-tabs').addEventListener('click', () => openTabManager());
  ref(view, 'invites').addEventListener('click', () => openInvites());
  ref(view, 'manage-tabs').hidden = !state.isOwner;
  ref(view, 'invites').hidden = !state.isOwner;

  const selectBtn = ref(view, 'select');
  selectBtn.addEventListener('click', () => setSelecting(view, !state.selecting));
  const dlSelected = ref(view, 'download-selected');
  dlSelected.hidden = true;
  dlSelected.addEventListener('click', () => downloadSelected(view));

  mount(view);
  state.view = view;
  watchSentinel(view);
  await loadTabs();
  if (!state.guest && state.pendingDialog) {   // upload clicked as guest: continue with the file dialog
    state.pendingDialog = false;
    uploadInput.click();
  }
}

function watchSentinel(view) {
  const sentinel = ref(view, 'sentinel');
  if (!sentinel) return;   // themes without a sentinel get the first page only
  new IntersectionObserver((entries) => {
    if (entries.some((e) => e.isIntersecting)) void loadMore();
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
  ref(state.view, 'tab-rename').hidden = !state.tab?.canRename;
  state.items = [];
  state.complete = false;
  ref(state.view, 'grid').replaceChildren();
  await loadMore();
}

// ---- tabs: create / rename / delete via a small dialog -------------------------------------------------
async function openTabEditor(tab) {
  const dialog = tpl('tab-editor');
  const name = ref(dialog, 'name');
  const message = ref(dialog, 'message');
  const del = ref(dialog, 'delete');
  const close = () => { document.removeEventListener('keydown', onKey); dialog.remove(); };
  const onKey = (e) => { if (e.key === 'Escape') close(); };
  ref(dialog, 'close').addEventListener('click', close);
  document.addEventListener('keydown', onKey);

  if (tab) {
    text(dialog, 'title', t('tabRenameTitle'));
    name.value = tab.name;
    let armed = false;
    if (tab.mine) {
      del.hidden = false;
      del.addEventListener('click', async () => {
        if (!armed) { armed = true; del.textContent = t('tabDeleteArmed'); return; }
        const res = await api(`/tabs/${tab.id}`, { method: 'DELETE' });
        if (!res.ok) { message.textContent = t('tabFail'); return; }
        close();
        state.tab = null;   // gone: fall back to the first tab
        await loadTabs();
      });
    }
  } else {
    text(dialog, 'title', t('tabCreateTitle'));
  }

  ref(dialog, 'form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const value = name.value.trim();
    if (!value) return;
    const res = tab
      ? await api(`/tabs/${tab.id}`, { method: 'PATCH', ...jsonBody({ name: value }) })
      : await api('/tabs', { method: 'POST', ...jsonBody({ name: value, personal: true }) });
    if (!res.ok) { message.textContent = res.status === 409 ? t('tabNameInUse') : t('tabFail'); return; }
    close();
    await loadTabs(tab ? tab.id : (await res.json()).id);   // select the saved / created tab
  });

  document.body.append(dialog);
  name.focus();
}

// ---- owner: manage tabs (sections, order, guest uploads) ------------------------------------------------
async function openTabManager() {
  const dialog = tpl('tab-manage');
  const list = ref(dialog, 'list');
  const message = ref(dialog, 'message');
  const close = () => { document.removeEventListener('keydown', onKey); dialog.remove(); };
  const onKey = (e) => { if (e.key === 'Escape') close(); };
  ref(dialog, 'close').addEventListener('click', close);
  document.addEventListener('keydown', onKey);

  const fail = (e) => { message.textContent = e ?? t('tabFail'); };

  const render = () => {
    const sections = state.tabs.filter((t2) => t2.kind === 'Section');
    list.replaceChildren();
    for (const t2 of state.tabs) {
      const row = tpl('tab-manage-row');
      row.dataset.kind = t2.kind.toLowerCase();
      text(row, 'label', `${t2.name} (${t2.count})`);
      const guests = ref(row, 'guests');
      const up = ref(row, 'up');
      const down = ref(row, 'down');
      if (t2.kind === 'Section') {
        guests.checked = t2.allowGuestUploads;
        guests.addEventListener('change', async () => {
          const res = await api(`/tabs/${t2.id}`, { method: 'PATCH', ...jsonBody({ allowGuestUploads: guests.checked }) });
          if (!res.ok) { fail(); return; }
          await refresh();
        });
        const pos = sections.indexOf(t2);
        up.disabled = pos === 0;
        down.disabled = pos === sections.length - 1;
        up.addEventListener('click', () => move(sections, pos, -1));
        down.addEventListener('click', () => move(sections, pos, +1));
      } else {
        guests.closest('label')?.setAttribute('hidden', '');
        up.hidden = true;
        down.hidden = true;
      }

      const del = ref(row, 'delete');
      let armed = false;
      del.addEventListener('click', async () => {
        if (!armed) { armed = true; row.dataset.armed = 'true'; del.textContent = t('tabDeleteArmed'); return; }
        const res = await api(`/tabs/${t2.id}`, { method: 'DELETE' });
        if (!res.ok) { fail(); return; }
        await refresh();
      });
      list.append(row);
    }
  };

  const move = async (sections, pos, delta) => {
    const ids = sections.map((t2) => t2.id);
    const j = pos + delta;
    if (j < 0 || j >= ids.length) return;
    [ids[pos], ids[j]] = [ids[j], ids[pos]];
    const res = await api('/tabs/order', { method: 'PUT', ...jsonBody({ ids }) });
    if (!res.ok) { fail(); return; }
    await refresh();
  };

  ref(dialog, 'form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const name = ref(dialog, 'name').value.trim();
    if (!name) return;
    const res = await api('/tabs', { method: 'POST', ...jsonBody({ name, allowGuestUploads: false }) });
    if (!res.ok) { fail(res.status === 409 ? t('tabNameInUse') : null); return; }
    ref(dialog, 'name').value = '';
    await refresh();
  });

  const refresh = async () => {
    await loadTabs(state.tab?.id);
    render();
  };

  document.body.append(dialog);
  await refresh();
}

// ---- owner: invite management ---------------------------------------------------------------------------
async function openInvites() {
  const dialog = tpl('invites');
  const list = ref(dialog, 'list');
  const message = ref(dialog, 'message');
  const close = () => { document.removeEventListener('keydown', onKey); dialog.remove(); };
  const onKey = (e) => { if (e.key === 'Escape') close(); };
  ref(dialog, 'close').addEventListener('click', close);
  document.addEventListener('keydown', onKey);

  const load = async () => {
    const invites = await (await api('/invites')).json();
    list.replaceChildren();
    ref(dialog, 'empty').hidden = invites.length > 0;
    for (const i of invites) {
      const row = tpl('invite-row');
      row.dataset.joined = String(i.joined);
      text(row, 'label', i.email);
      text(row, 'state', t(i.joined ? 'invitesJoined' : 'invitesPending'));
      ref(row, 'remove').addEventListener('click', async () => {
        const res = await api(`/invites/${i.id}`, { method: 'DELETE' });
        if (res.ok) await load();
      });
      list.append(row);
    }
  };

  ref(dialog, 'form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const email = ref(dialog, 'email').value.trim();
    if (!email) return;
    const res = await api('/invites', { method: 'POST', ...jsonBody({ email }) });
    if (!res.ok) { message.textContent = t('inviteFail'); return; }
    ref(dialog, 'email').value = '';
    await load();
  });

  document.body.append(dialog);
  await load();
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
  tile.dataset.candelete = String(m.canDelete === true);
  if (m.width && m.height) tile.style.setProperty('--ratio', `${m.width} / ${m.height}`);
  const img = ref(tile, 'image');
  img.src = m.status === 'Ready' ? `${base}/media/${m.id}/thumb` : '';
  img.alt = m.name;
  ref(tile, 'star').hidden = state.guest;
  ref(tile, 'open').addEventListener('click', () => (state.selecting ? toggleSelect(m, tile) : openLightbox(index)));
  ref(tile, 'star').addEventListener('click', () => toggleStar(m, tile));
  const del = ref(tile, 'delete');
  del.addEventListener('click', async () => {
    if (tile.dataset.armed !== 'true') { tile.dataset.armed = 'true'; del.textContent = t('mediaDeleteArmed'); return; }
    await deleteMedia(m, tile);
  });
  return tile;
}

// Moderation: remove a media item (uploader or owner, enforced server-side) and update the grid.
async function deleteMedia(m, tile) {
  const res = await api(`/media/${m.id}`, { method: 'DELETE' });
  if (!res.ok) return;
  state.items = state.items.filter((x) => x.id !== m.id);
  if (tile) tile.remove();
  else renderGrid();
  ref(state.view, 'empty').hidden = state.items.length > 0;
  await loadTabs(state.tab?.id);   // tab counts changed
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
  const uploadedMsg = t('uploaded', { count: finished });
  status.textContent = failed ? t('uploadFailed', { name: failed }) : uploadedMsg;
  await refreshUntilReady(tabId);
  await loadTabs(tabId);
  // The "preparing previews" note is stale once processing finished (or polling gave up).
  if (view.dataset.uploading !== 'true' && status.textContent === uploadedMsg) status.textContent = '';
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
    box.dataset.armed = 'false';
    ref(box, 'star').hidden = state.guest;
    del.hidden = !m.canDelete;
    del.textContent = '✕';
  };
  const close = () => {
    box.remove();
    document.removeEventListener('keydown', onKey);
    if (document.fullscreenElement) void document.exitFullscreen();
  };
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
  const del = ref(box, 'delete');
  del.addEventListener('click', async () => {
    const m = state.items[state.lightboxIndex];
    if (!m || !m.canDelete) return;
    if (box.dataset.armed !== 'true') { box.dataset.armed = 'true'; del.textContent = t('mediaDeleteArmed'); return; }
    close();
    await deleteMedia(m, null);
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

// ---- subscriptions: per-tab, per-channel notification settings -----------------------------------------
function tabName(id) {
  if (!id) return t('subsAllTabs');
  return state.tabs.find((x) => x.id === id)?.name ?? '?';
}

function subLabel(s) {
  const channel = s.channel === 'Email' ? t('subsEmail') : t('subsTelegram');
  const mode = s.mode === 'Digest' ? t('subsDigest') : t('subsInstant');
  return channel + ' · ' + tabName(s.tabId) + ' · ' + mode;
}

async function openSubscriptions() {
  const dialog = tpl('subscriptions');
  const list = ref(dialog, 'list');
  const message = ref(dialog, 'message');
  const close = () => { document.removeEventListener('keydown', onKey); dialog.remove(); };
  const onKey = (e) => { if (e.key === 'Escape') close(); };

  const load = async () => {
    const subs = await (await api('/subscriptions')).json();
    list.replaceChildren();
    ref(dialog, 'empty').hidden = subs.length > 0;
    for (const s of subs) {
      const row = tpl('sub-row');
      text(row, 'label', subLabel(s));
      ref(row, 'remove').addEventListener('click', async () => {
        const res = await api(`/subscriptions/${s.id}`, { method: 'DELETE' });
        if (res.ok) await load();
      });
      list.append(row);
    }
  };

  ref(dialog, 'close').addEventListener('click', close);
  document.addEventListener('keydown', onKey);

  ref(dialog, 'form').addEventListener('submit', async (e) => {
    e.preventDefault();
    message.replaceChildren();
    const channel = ref(dialog, 'channel').value;
    const tabId = ref(dialog, 'tab').value || null;
    const mode = ref(dialog, 'mode').value;
    if (channel === 'Telegram') {
      const query = tabId ? `?tab=${encodeURIComponent(tabId)}&mode=${encodeURIComponent(mode)}` : `?mode=${encodeURIComponent(mode)}`;
      const res = await api(`/subscriptions/telegram-link${query}`);
      if (!res.ok) { message.textContent = t('subsTelegramOff'); return; }
      const link = (await res.json()).url;
      const a = document.createElement('a');
      a.href = link;
      a.rel = 'noreferrer';
      a.textContent = link;
      const hint = document.createElement('span');
      hint.textContent = t('subsTelegramHint') + ' ';
      message.append(hint, a);
      return;
    }

    const res = await api('/subscriptions', { method: 'PUT', ...jsonBody({ channel, tabId, mode }) });
    if (!res.ok) { message.textContent = t('subsFail'); return; }
    await load();
  });

  const tabSelect = ref(dialog, 'tab');
  const all = document.createElement('option');
  all.value = '';
  all.textContent = t('subsAllTabs');
  tabSelect.append(all);
  for (const tb of state.tabs) {
    const option = document.createElement('option');
    option.value = tb.id;
    option.textContent = tb.name;
    tabSelect.append(option);
  }

  document.body.append(dialog);
  await load();
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
  state.isOwner = false;
  state.guest = !me.ok && !info.inviteOnly;   // open albums: browse without signing in
  if (!me.ok) {
    if (info.inviteOnly) return showLogin(info);   // invite-only keeps the login wall for viewing
  } else {
    state.isOwner = (await me.json()).isOwner === true;
  }
  return showGallery(info);
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
