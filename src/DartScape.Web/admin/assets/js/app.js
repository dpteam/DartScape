/* ============================================================================
 * DartScape Admin вЂ” assets/js/app.js
 * Role: all client behaviour for the LAN-only admin panel. Talks to the admin
 * API exactly as documented in docs/WEB-API.md using XMLHttpRequest and the
 * `Authorization: Bearer <token>` header. ES5 only, no frameworks, no build.
 *
 * Contents:
 *   1. Constants, state, DOM cache
 *   2. Small helpers (storage, escaping, formatting, classes)
 *   3. HTTP layer with auth header, timeout, JSON guard, 401 routing
 *   4. Login gate and session lifecycle
 *   5. Theme handling
 *   6. Tabs and polling scheduler
 *   7. Overview
 *   8. Players, kick, message
 *   9. Console, history, broadcast
 *  10. Log tail
 *  11. Config
 *  12. Dialog, toasts, keyboard
 *  13. Boot
 * ==========================================================================*/
(function () {
  'use strict';

  /* 1. CONSTANTS, STATE, DOM CACHE ---------------------------------------- */

  var TOKEN_KEY = 'dartscape.admin.token';
  var THEME_KEY = 'dartscape.admin.theme';
  var THEMES = ['light', 'dark', 'oled'];
  var TABS = { overview: true, players: true, console: true, logs: true, config: true };
  var NAV_ORDER = ['overview', 'players', 'console', 'logs', 'config'];

  var POLL_MS = { overview: 3000, players: 3000, logs: 2000 };
  var REQUEST_TIMEOUT_MS = 10000;
  var MAX_CONSOLE_LINES = 400;
  var MAX_LOG_LINES = 500;
  var MAX_HISTORY = 100;
  var MAX_TOASTS = 4;

  var state = {
    token: null,
    tab: 'overview',
    theme: 'light',
    players: [],
    playerFilter: '',
    logs: { since: 0, level: 'all', autoScroll: true, lines: [] },
    config: null,
    history: [],
    historyIndex: -1,
    historyDraft: '',
    consoleSeeded: false,
    busy: {},
    pollers: {},
    modal: { open: false, onConfirm: null, opener: null }
  };

  var dom = {};

  /* 2. SMALL HELPERS ------------------------------------------------------ */

  function storageGet(key) {
    try { return window.localStorage.getItem(key); } catch (e) { return null; }
  }

  function storageSet(key, value) {
    try { window.localStorage.setItem(key, value); } catch (e) { /* private mode */ }
  }

  function storageRemove(key) {
    try { window.localStorage.removeItem(key); } catch (e) { /* private mode */ }
  }

  function trim(value) {
    if (value === null || value === undefined) { return ''; }
    return String(value).replace(/^\s+|\s+$/g, '');
  }

  /* Escape every server-provided string that reaches an HTML template. */
  function esc(value) {
    if (value === null || value === undefined) { return ''; }
    return String(value).replace(/[&<>"']/g, function (ch) {
      if (ch === '&') { return '&amp;'; }
      if (ch === '<') { return '&lt;'; }
      if (ch === '>') { return '&gt;'; }
      if (ch === '"') { return '&quot;'; }
      return '&#39;';
    });
  }

  function pad2(n) {
    return n < 10 ? '0' + n : String(n);
  }

  function fmtClock(unixSec) {
    if (!unixSec) { return 'вЂ”'; }
    var d = new Date(unixSec * 1000);
    return pad2(d.getHours()) + ':' + pad2(d.getMinutes()) + ':' + pad2(d.getSeconds());
  }

  function fmtDateTime(unixSec) {
    if (!unixSec) { return 'вЂ”'; }
    var d = new Date(unixSec * 1000);
    return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()) +
      ' ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes());
  }

  function fmtDate(unixSec) {
    if (!unixSec) { return 'вЂ”'; }
    var d = new Date(unixSec * 1000);
    return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate());
  }

  function fmtDuration(sec) {
    if (sec === null || sec === undefined || isNaN(sec)) { return 'вЂ”'; }
    sec = Math.max(0, Math.floor(Number(sec)));
    var d = Math.floor(sec / 86400);
    var h = Math.floor((sec % 86400) / 3600);
    var m = Math.floor((sec % 3600) / 60);
    var s = sec % 60;
    if (d > 0) { return d + 'd ' + h + 'h ' + m + 'm'; }
    if (h > 0) { return h + 'h ' + m + 'm'; }
    if (m > 0) { return m + 'm ' + s + 's'; }
    return s + 's';
  }

  function fmtIdle(sec) {
    if (sec === null || sec === undefined || isNaN(sec)) { return 'вЂ”'; }
    sec = Math.max(0, Math.floor(Number(sec)));
    if (sec < 60) { return sec + 's'; }
    if (sec < 3600) { return Math.floor(sec / 60) + 'm ' + (sec % 60) + 's'; }
    return Math.floor(sec / 3600) + 'h ' + Math.floor((sec % 3600) / 60) + 'm';
  }

  function fmtNum(value) {
    if (value === null || value === undefined || value === '') { return 'вЂ”'; }
    var num = Number(value);
    if (isNaN(num)) { return String(value); }
    var negative = num < 0;
    var s = String(Math.abs(Math.round(num)));
    s = s.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
    return negative ? '-' + s : s;
  }

  function fmtFixed(value, places) {
    if (typeof value !== 'number' || isNaN(value)) { return 'вЂ”'; }
    return value.toFixed(places);
  }

  function setText(el, value) {
    if (!el) { return; }
    el.textContent = (value === null || value === undefined) ? 'вЂ”' : String(value);
  }

  function addClass(el, cls) {
    if (!el) { return; }
    if (el.classList) { el.classList.add(cls); return; }
    if (!hasClass(el, cls)) { el.className += ' ' + cls; }
  }

  function removeClass(el, cls) {
    if (!el) { return; }
    if (el.classList) { el.classList.remove(cls); return; }
    el.className = (' ' + el.className + ' ').replace(' ' + cls + ' ', ' ').replace(/^\s+|\s+$/g, '');
  }

  function hasClass(el, cls) {
    if (!el) { return false; }
    if (el.classList) { return el.classList.contains(cls); }
    return (' ' + el.className + ' ').indexOf(' ' + cls + ' ') !== -1;
  }

  function tdCell(text, cls, label) {
    var td = document.createElement('td');
    if (cls) { td.className = cls; }
    if (label) { td.setAttribute('data-label', label); }
    td.textContent = text;
    return td;
  }

  function nowSec() {
    return Math.floor(new Date().getTime() / 1000);
  }

  /* 3. HTTP LAYER --------------------------------------------------------- */

  var API = {
    request: function (method, path, body, cb, opts) {
      opts = opts || {};
      var xhr = new XMLHttpRequest();
      var finished = false;

      var timer = window.setTimeout(function () {
        if (finished) { return; }
        finished = true;
        try { xhr.abort(); } catch (e) { /* ignore */ }
        cb({ status: 0, error: 'timeout', message: 'The request timed out.' });
      }, opts.timeout || REQUEST_TIMEOUT_MS);

      function finish(err, data) {
        if (finished) { return; }
        finished = true;
        window.clearTimeout(timer);
        cb(err, data);
      }

      try {
        xhr.open(method, path, true);
      } catch (e) {
        finish({ status: 0, error: 'bad-url', message: 'Could not build the request.' });
        return;
      }

      xhr.setRequestHeader('Accept', 'application/json');
      if (state.token) {
        xhr.setRequestHeader('Authorization', 'Bearer ' + state.token);
      }
      if (body !== null && body !== undefined) {
        xhr.setRequestHeader('Content-Type', 'application/json; charset=utf-8');
      }

      xhr.onreadystatechange = function () {
        if (xhr.readyState !== 4) { return; }

        var text = xhr.responseText || '';
        var data = null;
        if (text) {
          try { data = JSON.parse(text); } catch (e) { data = null; }
        }

        /* JSON guard: never trust a non-JSON or empty body. */
        if (!data || typeof data !== 'object') {
          if (xhr.status === 0) {
            finish({ status: 0, error: 'network', message: 'Cannot reach the admin server.' });
            return;
          }
          finish({
            status: xhr.status,
            error: 'bad-response',
            message: 'Unexpected response from the server (' + xhr.status + ').'
          });
          return;
        }

        if (xhr.status >= 200 && xhr.status < 300 && data.ok === true) {
          finish(null, data);
          return;
        }

        finish({
          status: xhr.status,
          error: data.error || 'http-' + xhr.status,
          message: data.message || 'The server refused the request.'
        });
      };

      xhr.send((body !== null && body !== undefined) ? JSON.stringify(body) : null);
    },

    get: function (path, cb, opts) {
      API.request('GET', path, null, cb, opts);
    },

    post: function (path, body, cb, opts) {
      API.request('POST', path, body, cb, opts);
    }
  };

  /* Central error handling: 401 always drops back to the login gate. */
  function handleApiError(err, opts) {
    if (err.status === 401 && !opts.noAuthGate) {
      onAuthError();
      return true;
    }
    if (err.status === 0) {
      setConn('offline');
    } else if (err.status !== 401) {
      setConn('err');
    }
    if (!opts.silent) {
      toast('error', err.message || 'Request failed.');
    }
    return false;
  }

  function apiGet(path, cb, opts) {
    opts = opts || {};
    API.get(path, function (err, data) {
      if (err) {
        if (handleApiError(err, opts)) { return; }
        if (cb) { cb(err); }
        return;
      }
      if (!state.token && !opts.noAuthGate) { return; }
      setConn('ok');
      if (cb) { cb(null, data); }
    }, opts);
  }

  function apiPost(path, body, cb, opts) {
    opts = opts || {};
    API.post(path, body, function (err, data) {
      if (err) {
        if (handleApiError(err, opts)) { return; }
        if (cb) { cb(err); }
        return;
      }
      if (!state.token) { return; }
      setConn('ok');
      if (cb) { cb(null, data); }
    }, opts);
  }

  function setConn(kind) {
    state.conn = kind;
    if (!dom.connPill) { return; }
    dom.connPill.className = 'pill conn is-' + kind;
    var labels = { ok: 'Live', err: 'Error', offline: 'Offline', wait: 'Connecting', off: 'Offline' };
    dom.connText.textContent = labels[kind] || 'Unknown';
    dom.connPill.setAttribute('title', 'Last request: ' + (labels[kind] || 'unknown'));
  }

  /* 4. LOGIN GATE AND SESSION --------------------------------------------- */

  function setLoginStatus(message, info) {
    if (!dom.loginError) { return; }
    dom.loginError.textContent = message || '';
    dom.loginError.className = info ? 'form-error is-info' : 'form-error';
  }

  function showLogin(message) {
    dom.app.hidden = true;
    dom.loginGate.hidden = false;
    state.busy = {};
    dom.loginSubmit.disabled = false;
    dom.loginSubmit.textContent = 'Sign in';
    dom.tokenInput.value = '';
    /* Release any control a failed request left disabled. */
    if (dom.consoleInput) { dom.consoleInput.disabled = false; }
    setLoginStatus(message || '', false);
    window.setTimeout(function () {
      try { dom.tokenInput.focus(); } catch (e) { /* ignore */ }
    }, 0);
  }

  function attemptLogin(token, cb) {
    state.token = token;
    apiGet('api/admin/overview', function (err) {
      if (err) {
        state.token = null;
        cb(err);
        return;
      }
      cb(null);
    }, { noAuthGate: true, silent: true });
  }

  function onLoginSubmit(e) {
    e.preventDefault();
    if (state.busy.login) { return; }
    var token = trim(dom.tokenInput.value);
    if (!token) {
      setLoginStatus('Enter the admin token.');
      dom.tokenInput.focus();
      return;
    }

    state.busy.login = true;
    dom.loginSubmit.disabled = true;
    dom.loginSubmit.textContent = 'Signing inвЂ¦';
    setLoginStatus('', false);

    attemptLogin(token, function (err) {
      state.busy.login = false;
      dom.loginSubmit.disabled = false;
      dom.loginSubmit.textContent = 'Sign in';

      if (err) {
        if (err.status === 401) {
          setLoginStatus('Wrong token. Check config.ini [admin] token.');
        } else if (err.status === 429) {
          setLoginStatus('Too many failed attempts. Wait five minutes and try again.');
        } else if (err.status === 403) {
          setLoginStatus('This device is not allowed. The panel accepts private addresses only.');
        } else {
          setLoginStatus(err.message || 'Sign in failed.');
        }
        dom.tokenInput.focus();
        return;
      }

      if (dom.rememberToken.checked) {
        storageSet(TOKEN_KEY, token);
      } else {
        storageRemove(TOKEN_KEY);
      }
      enterApp();
      toast('success', 'Signed in.');
    });
  }

  function enterApp() {
    dom.loginGate.hidden = true;
    dom.app.hidden = false;
    dom.tokenInput.value = '';
    setLoginStatus('', false);

    state.busy = {};
    state.tab = 'overview';
    syncTabs();
    startPolling();

    if (!state.consoleSeeded) {
      appendConsole('sys', 'Admin console ready. Type a command and press Enter.');
      appendConsole('sys', 'Up / Down recalls previous commands.');
      state.consoleSeeded = true;
    }

    try { dom.navOverview.focus(); } catch (e) { /* ignore */ }
  }

  function onAuthError() {
    if (!state.token) { return; }
    state.token = null;
    state.busy = {};
    storageRemove(TOKEN_KEY);
    stopPolling();
    closeModal();
    setConn('off');
    dom.rememberToken.checked = false;
    showLogin('Your token was rejected. Sign in again.');
    toast('error', 'Session expired вЂ” sign in again.');
  }

  function signOut() {
    stopPolling();
    state.token = null;
    state.players = [];
    state.config = null;
    state.busy = {};
    state.consoleSeeded = false;
    state.history = [];
    state.historyIndex = -1;
    state.historyDraft = '';
    state.logs = { since: 0, level: 'all', autoScroll: true, lines: [] };

    storageRemove(TOKEN_KEY);
    closeModal();

    dom.consoleOut.innerHTML = '';
    dom.logOut.innerHTML = '';
    dom.playersBody.innerHTML = '';
    dom.configSections.innerHTML = '';
    dom.playersEmpty.hidden = false;
    dom.playerCount.textContent = '0';
    dom.playerFilter.value = '';
    dom.configSearch.value = '';
    state.playerFilter = '';
    dom.logCount.textContent = '0 lines';
    dom.logStatus.textContent = 'ConnectingвЂ¦';
    dom.configMeta.textContent = 'Read-only В· secrets redacted';
    dom.overviewUpdated.textContent = 'Waiting for data';
    dom.navPlayerCount.hidden = true;
    resetOverview();

    dom.app.hidden = true;
    dom.loginGate.hidden = false;
    dom.tokenInput.value = '';
    dom.rememberToken.checked = false;
    dom.consoleInput.disabled = false;
    dom.consoleInput.value = '';
    dom.broadcastInput.value = '';
    setLoginStatus('', false);
    setConn('off');
    dom.tokenInput.focus();
  }

  function bootAuth() {
    var saved = storageGet(TOKEN_KEY);
    if (!saved) {
      dom.tokenInput.focus();
      return;
    }

    dom.rememberToken.checked = true;
    dom.loginSubmit.disabled = true;
    dom.loginSubmit.textContent = 'CheckingвЂ¦';
    setLoginStatus('Checking saved tokenвЂ¦', true);

    attemptLogin(saved, function (err) {
      dom.loginSubmit.disabled = false;
      dom.loginSubmit.textContent = 'Sign in';

      if (err) {
        storageRemove(TOKEN_KEY);
        dom.rememberToken.checked = false;
        if (err.status === 429) {
          setLoginStatus('Too many failed attempts. Wait five minutes and try again.');
        } else if (err.status === 403) {
          setLoginStatus('This device is not allowed. The panel accepts private addresses only.');
        } else if (err.status === 0) {
          setLoginStatus('Cannot reach the admin server. Is DartScape.Web --admin running?');
        } else {
          setLoginStatus('The saved token was rejected. Enter it again.');
        }
        dom.tokenInput.focus();
        return;
      }

      enterApp();
    });
  }

  /* 5. THEME -------------------------------------------------------------- */

  function applyTheme(name, persist) {
    var valid = false;
    for (var i = 0; i < THEMES.length; i++) {
      if (THEMES[i] === name) { valid = true; }
    }
    if (!valid) { name = 'light'; }

    state.theme = name;
    document.documentElement.setAttribute('data-theme', name);
    if (persist) { storageSet(THEME_KEY, name); }

    var buttons = document.querySelectorAll('[data-theme-set]');
    for (var j = 0; j < buttons.length; j++) {
      var btn = buttons[j];
      var active = btn.getAttribute('data-theme-set') === name;
      if (active) { addClass(btn, 'is-active'); } else { removeClass(btn, 'is-active'); }
      btn.setAttribute('aria-pressed', active ? 'true' : 'false');
    }

    var meta = document.querySelector('meta[name="theme-color"]');
    if (meta) {
      meta.setAttribute('content', name === 'dark' ? '#16202c' : (name === 'oled' ? '#000000' : '#4a76a8'));
    }
  }

  /* 6. TABS AND POLLING --------------------------------------------------- */

  function syncTabs() {
    for (var i = 0; i < NAV_ORDER.length; i++) {
      var name = NAV_ORDER[i];
      var nav = document.getElementById('nav-' + name);
      var panel = document.getElementById('tab-' + name);
      var active = name === state.tab;

      if (nav) {
        if (active) { addClass(nav, 'is-active'); } else { removeClass(nav, 'is-active'); }
        nav.setAttribute('aria-selected', active ? 'true' : 'false');
        nav.setAttribute('tabindex', active ? '0' : '-1');
      }
      if (panel) {
        if (active) {
          addClass(panel, 'is-active');
          panel.hidden = false;
        } else {
          removeClass(panel, 'is-active');
          panel.hidden = true;
        }
      }
    }
  }

  function switchTab(name) {
    if (!TABS[name]) { return; }
    state.tab = name;
    syncTabs();
    if (name === 'config' && !state.config) {
      loadConfig(false);
    }
    startPolling();
  }

  function isHidden() {
    return document.hidden === true || document.webkitHidden === true;
  }

  function startPolling() {
    stopPolling();
    if (!state.token || isHidden()) { return; }

    if (state.tab === 'overview') {
      loadOverview();
      state.pollers.overview = window.setInterval(loadOverview, POLL_MS.overview);
    } else if (state.tab === 'players') {
      loadPlayers();
      state.pollers.players = window.setInterval(loadPlayers, POLL_MS.players);
    } else if (state.tab === 'logs') {
      pollLogs();
      state.pollers.logs = window.setInterval(pollLogs, POLL_MS.logs);
    }
  }

  function stopPolling() {
    for (var key in state.pollers) {
      if (state.pollers.hasOwnProperty(key) && state.pollers[key]) {
        window.clearInterval(state.pollers[key]);
      }
    }
    state.pollers = {};
  }

  function onVisibilityChange() {
    if (!state.token) { return; }
    if (isHidden()) {
      stopPolling();
    } else {
      startPolling();
    }
  }

  /* 7. OVERVIEW ----------------------------------------------------------- */

  function loadOverview() {
    if (state.busy.overview || !state.token) { return; }
    state.busy.overview = true;

    apiGet('api/admin/overview', function (err, data) {
      state.busy.overview = false;
      if (err) {
        dom.overviewUpdated.textContent = 'Update failed';
        return;
      }
      renderOverview(data);
    }, { silent: true });
  }

  function renderOverview(data) {
    var server = data.server || {};
    var world = data.world || {};
    var proc = data.process || {};

    setText(dom.ovUptime, fmtDuration(server.uptimeSec));
    setText(dom.ovStarted, proc.startedAt ? 'since ' + fmtDate(proc.startedAt) : 'since вЂ”');
    setText(dom.ovPlayers, fmtNum(world.playersOnline) + ' / ' + fmtNum(world.playersMax));
    setText(dom.ovAccountsStat, fmtNum(world.accountsTotal) + ' accounts');
    setText(dom.ovTickRate, fmtFixed(server.tickRate, 2));
    setText(dom.ovTickMs, fmtFixed(server.averageTickMs, 1));
    setText(dom.ovMemory, fmtFixed(server.memoryMb, 1) + ' MB');
    setText(dom.ovThreads, fmtNum(server.threads));
    setText(dom.ovRestarts, fmtNum(proc.restarts));

    setText(dom.ovPid, (proc.pid === null || proc.pid === undefined) ? 'вЂ”' : proc.pid);
    setText(dom.ovStartedAt, fmtDateTime(proc.startedAt));
    setText(dom.ovLogFile, proc.logFile || 'вЂ”');
    setText(dom.ovGc, fmtNum(server.gcCollections));
    setText(dom.ovNpcs, fmtNum(world.npcs));
    setText(dom.ovGround, fmtNum(world.groundItems));
    setText(dom.ovAccounts, fmtNum(world.accountsTotal));

    var online = server.online === true;
    dom.ovServerState.textContent = online ? 'Online' : 'Offline';
    dom.ovServerState.className = 'pill ' + (online ? 'is-ok' : 'is-offline');

    if (proc.lastError) {
      dom.ovLastError.textContent = String(proc.lastError);
      addClass(dom.ovLastError, 'has-error');
    } else {
      dom.ovLastError.textContent = 'None recorded.';
      removeClass(dom.ovLastError, 'has-error');
    }

    var count = typeof world.playersOnline === 'number' ? world.playersOnline : 0;
    setPlayerBadge(count);

    dom.overviewUpdated.textContent = 'Updated ' + fmtClock(nowSec());
  }

  function setPlayerBadge(count) {
    if (!dom.navPlayerCount) { return; }
    if (count > 0) {
      dom.navPlayerCount.textContent = String(count);
      dom.navPlayerCount.hidden = false;
    } else {
      dom.navPlayerCount.hidden = true;
    }
  }

  /* Blank the overview so a signed-out panel never shows stale numbers. */
  function resetOverview() {
    var ids = ['ovUptime', 'ovPlayers', 'ovTickRate', 'ovTickMs', 'ovMemory',
      'ovThreads', 'ovRestarts', 'ovPid', 'ovStartedAt', 'ovGc', 'ovNpcs',
      'ovGround', 'ovAccounts'];
    for (var i = 0; i < ids.length; i++) {
      setText(dom[ids[i]], 'вЂ”');
    }
    setText(dom.ovStarted, 'since вЂ”');
    setText(dom.ovAccountsStat, 'вЂ” accounts');
    setText(dom.ovLogFile, 'вЂ”');
    dom.ovLastError.textContent = 'None recorded.';
    removeClass(dom.ovLastError, 'has-error');
    dom.ovServerState.textContent = 'Unknown';
    dom.ovServerState.className = 'pill';
  }

  /* 8. PLAYERS ------------------------------------------------------------ */

  function loadPlayers() {
    if (state.busy.players || !state.token) { return; }
    state.busy.players = true;

    apiGet('api/admin/players', function (err, data) {
      state.busy.players = false;
      if (err) { return; }
      state.players = (data && data.players) || [];
      renderPlayers();
    }, { silent: true });
  }

  function renderPlayers() {
    var all = state.players;
    var raw = state.playerFilter;
    var q = trim(raw).toLowerCase();
    var shown = [];

    for (var i = 0; i < all.length; i++) {
      var p = all[i] || {};
      var name = String(p.name === null || p.name === undefined ? '' : p.name).toLowerCase();
      var addr = String(p.address === null || p.address === undefined ? '' : p.address).toLowerCase();
      if (!q || name.indexOf(q) !== -1 || addr.indexOf(q) !== -1) {
        shown.push(p);
      }
    }

    dom.playersBody.innerHTML = '';
    var frag = document.createDocumentFragment();
    for (var j = 0; j < shown.length; j++) {
      frag.appendChild(buildPlayerRow(shown[j]));
    }
    dom.playersBody.appendChild(frag);

    dom.playerCount.textContent = q ? (shown.length + ' / ' + all.length) : String(all.length);
    if (!q) { setPlayerBadge(all.length); }

    if (shown.length === 0) {
      dom.playersEmpty.hidden = false;
      dom.playersEmpty.textContent = all.length === 0
        ? 'No players online.'
        : 'No players match вЂњ' + raw + 'вЂќ.';
    } else {
      dom.playersEmpty.hidden = true;
    }
  }

  function buildPlayerRow(p) {
    var tr = document.createElement('tr');
    tr.setAttribute('data-name', p.name === null || p.name === undefined ? '' : String(p.name));

    var tdName = document.createElement('td');
    tdName.className = 'cell-name';
    tdName.appendChild(document.createTextNode(
      p.name === null || p.name === undefined ? 'вЂ”' : String(p.name)
    ));

    var priv = Number(p.privilege || 0);
    if (priv >= 1) {
      var badge = document.createElement('span');
      badge.className = 'priv';
      badge.textContent = priv >= 2 ? 'admin' : 'mod';
      tdName.appendChild(badge);
    }
    tr.appendChild(tdName);

    tr.appendChild(tdCell(fmtNum(p.combat), 'cell-num', 'Combat'));
    tr.appendChild(tdCell(fmtNum(p.totalLevel), 'cell-num', 'Level'));

    var pos = 'вЂ”';
    if (p.x !== null && p.x !== undefined && p.y !== null && p.y !== undefined) {
      pos = p.x + ', ' + p.y + (p.plane ? ' В· P' + p.plane : '');
    }
    tr.appendChild(tdCell(pos, 'cell-mono', 'Pos'));

    tr.appendChild(tdCell(fmtDuration(p.sessionSec), 'cell-num', 'Session'));
    tr.appendChild(tdCell(p.address || 'вЂ”', 'cell-mono', 'Addr'));
    tr.appendChild(tdCell(fmtIdle(p.idleSec), 'cell-num', 'Idle'));

    var tdActions = document.createElement('td');
    tdActions.className = 'col-actions';
    var wrap = document.createElement('div');
    wrap.className = 'row-actions';

    var msg = document.createElement('button');
    msg.type = 'button';
    msg.className = 'btn btn-xs';
    msg.setAttribute('data-action', 'message');
    msg.textContent = 'Message';

    var kick = document.createElement('button');
    kick.type = 'button';
    kick.className = 'btn btn-xs btn-danger';
    kick.setAttribute('data-action', 'kick');
    kick.textContent = 'Kick';

    wrap.appendChild(msg);
    wrap.appendChild(kick);
    tdActions.appendChild(wrap);
    tr.appendChild(tdActions);

    return tr;
  }

  function onPlayersClick(e) {
    var target = e.target;
    while (target && target !== dom.playersBody && target.tagName !== 'BUTTON') {
      target = target.parentNode;
    }
    if (!target || target === dom.playersBody) { return; }

    var action = target.getAttribute('data-action');
    if (!action) { return; }

    var row = target;
    while (row && row.tagName !== 'TR') { row = row.parentNode; }
    var name = row ? row.getAttribute('data-name') : '';
    if (!name) { return; }

    if (action === 'kick') { kickPlayer(name); }
    else if (action === 'message') { messagePlayer(name); }
  }

  function kickPlayer(name) {
    openConfirm({
      title: 'Kick ' + name + '?',
      message: name + ' will be disconnected immediately. They can log back in.',
      confirmLabel: 'Kick player',
      danger: true,
      onConfirm: function (done) {
        apiPost('api/admin/kick', { name: name, reason: 'Kicked by admin' }, function (err) {
          done();
          if (err) {
            if (err.status === 404) { loadPlayers(); }
            return;
          }
          closeModal();
          toast('success', name + ' was kicked.');
          appendConsole('sys', 'Kicked ' + name + '.');
          loadPlayers();
        });
      }
    });
  }

  function messagePlayer(name) {
    var wrap = document.createElement('div');

    var intro = document.createElement('p');
    intro.className = 'modal-text';
    intro.textContent = 'Send a private message to ' + name + '.';

    var label = document.createElement('label');
    label.className = 'field-label';
    label.setAttribute('for', 'pmText');
    label.textContent = 'Message';

    var input = document.createElement('textarea');
    input.id = 'pmText';
    input.className = 'input';
    input.rows = 3;
    input.maxLength = 240;
    input.placeholder = 'HelloвЂ¦';
    input.addEventListener('keydown', function (e) {
      if ((e.ctrlKey || e.metaKey) && e.keyCode === 13) {
        e.preventDefault();
        dom.modalConfirm.click();
      }
    });

    var hint = document.createElement('p');
    hint.className = 'hint';
    hint.textContent = 'Sent through the command dispatcher as вЂњ::msg ' + name +
      ' <text>вЂќ, so the reply appears in the Console tab.';

    wrap.appendChild(intro);
    wrap.appendChild(label);
    wrap.appendChild(input);
    wrap.appendChild(hint);

    openModal({
      title: 'Message ' + name,
      body: wrap,
      confirmLabel: 'Send',
      focus: input,
      onConfirm: function (done) {
        var text = trim(input.value);
        if (!text) {
          toast('warn', 'Type a message first.');
          done();
          input.focus();
          return;
        }
        var command = '::msg ' + name + ' ' + text;
        apiPost('api/admin/command', { text: command }, function (err, data) {
          done();
          if (err) {
            input.focus();
            return;
          }
          closeModal();
          appendConsole('cmd', '> ' + command);
          var output = (data && data.output) || [];
          if (output.length) {
            for (var i = 0; i < output.length; i++) { appendConsole('out', output[i]); }
          } else {
            appendConsole('sys', '(no output)');
          }
          toast('success', 'Message sent to ' + name + '.');
        });
      }
    });
  }

  /* 9. CONSOLE, HISTORY, BROADCAST ---------------------------------------- */

  function appendConsole(kind, text) {
    if (!dom.consoleOut) { return; }

    var line = document.createElement('div');
    line.className = 'console-line is-' + kind;

    var time = document.createElement('span');
    time.className = 'console-time';
    time.textContent = fmtClock(nowSec());

    var body = document.createElement('span');
    body.className = 'console-text';
    body.textContent = (text === null || text === undefined) ? '' : String(text);

    line.appendChild(time);
    line.appendChild(body);
    dom.consoleOut.appendChild(line);

    while (dom.consoleOut.childNodes.length > MAX_CONSOLE_LINES) {
      dom.consoleOut.removeChild(dom.consoleOut.firstChild);
    }
    dom.consoleOut.scrollTop = dom.consoleOut.scrollHeight;
  }

  function pushHistory(text) {
    if (state.history.length && state.history[state.history.length - 1] === text) {
      state.historyIndex = -1;
      return;
    }
    state.history.push(text);
    if (state.history.length > MAX_HISTORY) { state.history.shift(); }
    state.historyIndex = -1;
    state.historyDraft = '';
  }

  function setConsoleValue(value) {
    dom.consoleInput.value = value;
    try {
      dom.consoleInput.setSelectionRange(value.length, value.length);
    } catch (e) { /* ignore */ }
  }

  function historyPrev() {
    if (!state.history.length) { return; }
    if (state.historyIndex === -1) {
      state.historyDraft = dom.consoleInput.value;
      state.historyIndex = state.history.length - 1;
    } else if (state.historyIndex > 0) {
      state.historyIndex -= 1;
    }
    setConsoleValue(state.history[state.historyIndex]);
  }

  function historyNext() {
    if (!state.history.length || state.historyIndex === -1) { return; }
    if (state.historyIndex < state.history.length - 1) {
      state.historyIndex += 1;
      setConsoleValue(state.history[state.historyIndex]);
    } else {
      state.historyIndex = -1;
      setConsoleValue(state.historyDraft);
    }
  }

  function runCommand(raw) {
    var text = trim(raw);
    if (!text) { return; }

    appendConsole('cmd', '> ' + text);
    pushHistory(text);
    dom.consoleInput.value = '';
    dom.consoleInput.disabled = true;

    apiPost('api/admin/command', { text: text }, function (err, data) {
      dom.consoleInput.disabled = false;
      dom.consoleInput.focus();

      if (err) {
        appendConsole('err', err.message || 'Command failed.');
        return;
      }

      var output = data && data.output;
      if (output && output.length) {
        for (var i = 0; i < output.length; i++) {
          appendConsole('out', output[i]);
        }
      } else {
        appendConsole('sys', '(no output)');
      }
    }, { silent: true });
  }

  function onConsoleSubmit(e) {
    e.preventDefault();
    runCommand(dom.consoleInput.value);
  }

  function onConsoleKey(e) {
    if (e.keyCode === 38) {
      e.preventDefault();
      historyPrev();
    } else if (e.keyCode === 40) {
      e.preventDefault();
      historyNext();
    } else if (e.keyCode === 13) {
      /* Submit explicitly: implicit form submission is unreliable on some
         mobile keyboards and never fires mid-IME composition. */
      e.preventDefault();
      runCommand(dom.consoleInput.value);
    }
  }

  function submitBroadcast(raw) {
    var text = trim(raw);
    if (!text) {
      toast('warn', 'Enter a message to broadcast.');
      dom.broadcastInput.focus();
      return;
    }

    openConfirm({
      title: 'Broadcast to all players?',
      message: 'вЂњ' + text + 'вЂќ will appear in every online playerвЂ™s chat.',
      confirmLabel: 'Send broadcast',
      onConfirm: function (done) {
        apiPost('api/admin/broadcast', { text: text }, function (err, data) {
          done();
          if (err) { return; }
          closeModal();
          dom.broadcastInput.value = '';
          var n = (data && typeof data.delivered === 'number') ? data.delivered : 0;
          var plural = n === 1 ? ' player' : ' players';
          toast('success', 'Broadcast delivered to ' + n + plural + '.');
          appendConsole('sys', 'Broadcast delivered to ' + n + plural + '.');
        });
      }
    });
  }

  function onBroadcastSubmit(e) {
    e.preventDefault();
    submitBroadcast(dom.broadcastInput.value);
  }

  function onBroadcastKey(e) {
    if (e.keyCode === 13) {
      e.preventDefault();
      submitBroadcast(dom.broadcastInput.value);
    }
  }

  /* 10. LOG TAIL ---------------------------------------------------------- */

  function logLevelOf(line) {
    return String(line && line.level ? line.level : 'info').toLowerCase();
  }

  function matchesLogFilter(line) {
    return state.logs.level === 'all' || logLevelOf(line) === state.logs.level;
  }

  function makeLogRow(line) {
    var lvl = logLevelOf(line);
    var known = lvl === 'debug' || lvl === 'info' || lvl === 'warn' || lvl === 'error';
    var cls = known ? lvl : 'other';

    var row = document.createElement('div');
    row.className = 'log-line lv-row-' + cls;

    /* Every value below comes from the server and goes through esc(). */
    row.innerHTML =
      '<span class="log-time">' + esc(fmtClock(line.at)) + '</span>' +
      '<span class="log-level lv-' + cls + '">' + esc(lvl.toUpperCase()) + '</span>' +
      (line.category ? '<span class="log-cat">' + esc(line.category) + '</span>' : '') +
      '<span class="log-text">' + esc(line.text) + '</span>';

    return row;
  }

  function scrollLogs() {
    dom.logOut.scrollTop = dom.logOut.scrollHeight;
  }

  function updateLogCount() {
    var total = state.logs.lines.length;
    var visible = 0;
    for (var i = 0; i < total; i++) {
      if (matchesLogFilter(state.logs.lines[i])) { visible += 1; }
    }
    if (state.logs.level === 'all') {
      dom.logCount.textContent = total + (total === 1 ? ' line' : ' lines');
    } else {
      dom.logCount.textContent = visible + ' of ' + total + ' lines';
    }
  }

  function appendLogLines(lines) {
    var frag = document.createDocumentFragment();
    var added = 0;

    for (var i = 0; i < lines.length; i++) {
      var line = lines[i] || {};
      state.logs.lines.push(line);
      if (matchesLogFilter(line)) {
        frag.appendChild(makeLogRow(line));
        added += 1;
      }
    }

    while (state.logs.lines.length > MAX_LOG_LINES) { state.logs.lines.shift(); }

    dom.logOut.appendChild(frag);
    while (dom.logOut.childNodes.length > MAX_LOG_LINES) {
      dom.logOut.removeChild(dom.logOut.firstChild);
    }

    updateLogCount();
    if (state.logs.autoScroll && added > 0) { scrollLogs(); }
  }

  function renderLogs() {
    var frag = document.createDocumentFragment();
    for (var i = 0; i < state.logs.lines.length; i++) {
      if (matchesLogFilter(state.logs.lines[i])) {
        frag.appendChild(makeLogRow(state.logs.lines[i]));
      }
    }
    dom.logOut.innerHTML = '';
    dom.logOut.appendChild(frag);
    updateLogCount();
    if (state.logs.autoScroll) { scrollLogs(); }
  }

  function pollLogs() {
    if (state.busy.logs || !state.token) { return; }
    state.busy.logs = true;

    var path = 'api/admin/logs?since=' + encodeURIComponent(state.logs.since) + '&limit=200';

    apiGet(path, function (err, data) {
      state.busy.logs = false;

      if (err) {
        dom.logStatus.textContent = 'Paused вЂ” last request failed';
        return;
      }

      if (typeof data.nextSince === 'number') {
        state.logs.since = data.nextSince;
      }
      if (data.lines && data.lines.length) {
        appendLogLines(data.lines);
      }
      dom.logStatus.textContent = 'Live';
    }, { silent: true });
  }

  function onLogLevelClick(e) {
    var target = e.target;
    while (target && target !== dom.logLevels && target.tagName !== 'BUTTON') {
      target = target.parentNode;
    }
    if (!target || target === dom.logLevels) { return; }

    var level = target.getAttribute('data-level');
    if (!level) { return; }

    state.logs.level = level;
    var buttons = dom.logLevels.getElementsByTagName('button');
    for (var i = 0; i < buttons.length; i++) {
      var active = buttons[i] === target;
      if (active) { addClass(buttons[i], 'is-active'); } else { removeClass(buttons[i], 'is-active'); }
      buttons[i].setAttribute('aria-pressed', active ? 'true' : 'false');
    }
    renderLogs();
  }

  function onLogScroll() {
    var el = dom.logOut;
    var atBottom = (el.scrollHeight - el.scrollTop - el.clientHeight) < 24;

    if (atBottom && !state.logs.autoScroll) {
      state.logs.autoScroll = true;
      dom.logAutoScroll.checked = true;
    } else if (!atBottom && state.logs.autoScroll) {
      state.logs.autoScroll = false;
      dom.logAutoScroll.checked = false;
    }
  }

  function onAutoScrollChange() {
    state.logs.autoScroll = dom.logAutoScroll.checked;
    if (state.logs.autoScroll) { scrollLogs(); }
  }

  function clearLogs() {
    state.logs.lines = [];
    dom.logOut.innerHTML = '';
    updateLogCount();
    dom.logStatus.textContent = 'Live';
  }

  /* 11. CONFIG ------------------------------------------------------------ */

  function loadConfig(force) {
    if (!state.token) { return; }
    if (state.config && !force) {
      renderConfig();
      return;
    }
    if (state.busy.config) { return; }
    state.busy.config = true;

    apiGet('api/admin/config', function (err, data) {
      state.busy.config = false;
      if (err) { return; }
      state.config = (data && data.sections) || [];
      renderConfig();
    });
  }

  function renderConfig() {
    var host = dom.configSections;
    var raw = trim(dom.configSearch.value);
    var q = raw.toLowerCase();
    var sections = state.config || [];
    var shownEntries = 0;

    host.innerHTML = '';

    for (var i = 0; i < sections.length; i++) {
      var section = sections[i] || {};
      var name = section.name === null || section.name === undefined ? 'section' : String(section.name);
      var entries = section.entries || [];
      var rows = [];

      for (var j = 0; j < entries.length; j++) {
        var entry = entries[j] || {};
        var key = entry.key === null || entry.key === undefined ? '' : String(entry.key);
        var value = entry.value === null || entry.value === undefined ? '' : String(entry.value);
        var haystack = (name + ' ' + key + ' ' + value).toLowerCase();
        if (!q || haystack.indexOf(q) !== -1) {
          rows.push({ key: key, value: value });
        }
      }

      if (!rows.length) { continue; }

      var panel = document.createElement('div');
      panel.className = 'panel config-section';

      var head = document.createElement('div');
      head.className = 'panel-head';
      var title = document.createElement('h3');
      title.textContent = name;
      var count = document.createElement('span');
      count.className = 'muted small';
      count.textContent = rows.length + (rows.length === 1 ? ' entry' : ' entries');
      head.appendChild(title);
      head.appendChild(count);

      var wrap = document.createElement('div');
      wrap.className = 'table-wrap';
      var table = document.createElement('table');
      table.className = 'table';

      var thead = document.createElement('thead');
      var headRow = document.createElement('tr');
      var thKey = document.createElement('th');
      thKey.setAttribute('scope', 'col');
      thKey.textContent = 'Key';
      var thValue = document.createElement('th');
      thValue.setAttribute('scope', 'col');
      thValue.textContent = 'Value';
      headRow.appendChild(thKey);
      headRow.appendChild(thValue);
      thead.appendChild(headRow);

      var tbody = document.createElement('tbody');
      for (var k = 0; k < rows.length; k++) {
        var tr = document.createElement('tr');
        var tdKey = document.createElement('td');
        tdKey.className = 'config-key';
        tdKey.textContent = rows[k].key;
        var tdValue = document.createElement('td');
        tdValue.className = 'config-val';
        tdValue.textContent = rows[k].value;
        tr.appendChild(tdKey);
        tr.appendChild(tdValue);
        tbody.appendChild(tr);
      }

      table.appendChild(thead);
      table.appendChild(tbody);
      wrap.appendChild(table);
      panel.appendChild(head);
      panel.appendChild(wrap);
      host.appendChild(panel);

      shownEntries += rows.length;
    }

    if (shownEntries === 0) {
      dom.configEmpty.hidden = false;
      dom.configEmpty.textContent = sections.length === 0
        ? 'No configuration entries.'
        : 'No entries match вЂњ' + raw + 'вЂќ.';
    } else {
      dom.configEmpty.hidden = true;
    }

    dom.configMeta.textContent = 'Read-only В· secrets redacted В· ' + shownEntries +
      (shownEntries === 1 ? ' entry' : ' entries');
  }

  /* 12. DIALOG, TOASTS, KEYBOARD ------------------------------------------ */

  function openModal(opts) {
    state.modal.open = true;
    state.modal.opener = document.activeElement;
    state.modal.onConfirm = opts.onConfirm || null;

    dom.modalTitle.textContent = opts.title || 'Confirm';

    dom.modalBody.innerHTML = '';
    if (opts.body) { dom.modalBody.appendChild(opts.body); }

    dom.modalConfirm.textContent = opts.confirmLabel || 'Confirm';
    dom.modalConfirm.className = 'btn ' + (opts.danger ? 'btn-danger-solid' : 'btn-primary');
    dom.modalConfirm.disabled = false;

    dom.modal.hidden = false;
    addClass(document.body, 'modal-open');

    var focusTarget = opts.focus || dom.modalConfirm;
    window.setTimeout(function () {
      try { focusTarget.focus(); } catch (e) { /* ignore */ }
    }, 0);
  }

  function closeModal() {
    if (!state.modal.open) { return; }
    state.modal.open = false;
    dom.modal.hidden = true;
    dom.modalBody.innerHTML = '';
    removeClass(document.body, 'modal-open');

    var opener = state.modal.opener;
    state.modal.opener = null;
    state.modal.onConfirm = null;

    if (opener && opener.focus) {
      try { opener.focus(); } catch (e) { /* ignore */ }
    }
  }

  function openConfirm(opts) {
    var body = document.createElement('p');
    body.className = 'modal-text';
    body.textContent = opts.message || '';

    openModal({
      title: opts.title,
      body: body,
      confirmLabel: opts.confirmLabel,
      danger: opts.danger,
      onConfirm: opts.onConfirm
    });
  }

  function onModalConfirm() {
    var fn = state.modal.onConfirm;
    if (!fn) {
      closeModal();
      return;
    }
    dom.modalConfirm.disabled = true;
    fn(function done() {
      dom.modalConfirm.disabled = false;
    });
  }

  function trapFocus(e) {
    var nodes = dom.modal.querySelectorAll('button, input, textarea, select, a[href]');
    var list = [];
    for (var i = 0; i < nodes.length; i++) {
      if (!nodes[i].disabled && nodes[i].offsetParent !== null) { list.push(nodes[i]); }
    }
    if (!list.length) { return; }

    var first = list[0];
    var last = list[list.length - 1];

    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  }

  function onGlobalKey(e) {
    if (!state.modal.open) { return; }
    var key = e.keyCode;

    if (key === 27) {
      e.preventDefault();
      closeModal();
      return;
    }

    if (key === 9) {
      trapFocus(e);
      return;
    }

    if (key === 13) {
      var tag = e.target && e.target.tagName ? e.target.tagName : '';
      if (tag !== 'TEXTAREA' && tag !== 'BUTTON') {
        e.preventDefault();
        dom.modalConfirm.click();
      }
    }
  }

  function toast(kind, message) {
    if (!dom.toasts) { return; }

    while (dom.toasts.children.length >= MAX_TOASTS) {
      dom.toasts.removeChild(dom.toasts.firstChild);
    }

    var node = document.createElement('div');
    node.className = 'toast is-' + (kind === 'error' ? 'error' : (kind === 'warn' ? 'warn' : 'success'));
    node.setAttribute('role', kind === 'error' ? 'alert' : 'status');

    var mark = document.createElement('span');
    mark.className = 'toast-mark';
    mark.setAttribute('aria-hidden', 'true');
    mark.textContent = kind === 'success' ? 'вњ“' : '!';

    var body = document.createElement('span');
    body.className = 'toast-text';
    body.textContent = message === null || message === undefined ? '' : String(message);

    node.appendChild(mark);
    node.appendChild(body);
    dom.toasts.appendChild(node);

    window.setTimeout(function () {
      addClass(node, 'is-out');
      window.setTimeout(function () {
        if (node.parentNode) { node.parentNode.removeChild(node); }
      }, 220);
    }, 4200);
  }

  /* 13. BOOT -------------------------------------------------------------- */

  function collectDom() {
    dom.loginGate = document.getElementById('loginGate');
    dom.loginForm = document.getElementById('loginForm');
    dom.tokenInput = document.getElementById('tokenInput');
    dom.rememberToken = document.getElementById('rememberToken');
    dom.loginSubmit = document.getElementById('loginSubmit');
    dom.loginError = document.getElementById('loginError');

    dom.app = document.getElementById('app');
    dom.connPill = document.getElementById('connPill');
    dom.connText = document.getElementById('connText');
    dom.signOutBtn = document.getElementById('signOutBtn');
    dom.sidebar = document.getElementById('sidebar');
    dom.navOverview = document.getElementById('nav-overview');

    dom.overviewRefresh = document.getElementById('overviewRefresh');
    dom.overviewUpdated = document.getElementById('overviewUpdated');
    dom.ovUptime = document.getElementById('ovUptime');
    dom.ovStarted = document.getElementById('ovStarted');
    dom.ovPlayers = document.getElementById('ovPlayers');
    dom.ovAccountsStat = document.getElementById('ovAccountsStat');
    dom.ovTickRate = document.getElementById('ovTickRate');
    dom.ovTickMs = document.getElementById('ovTickMs');
    dom.ovMemory = document.getElementById('ovMemory');
    dom.ovThreads = document.getElementById('ovThreads');
    dom.ovRestarts = document.getElementById('ovRestarts');
    dom.ovServerState = document.getElementById('ovServerState');
    dom.ovPid = document.getElementById('ovPid');
    dom.ovStartedAt = document.getElementById('ovStartedAt');
    dom.ovLogFile = document.getElementById('ovLogFile');
    dom.ovGc = document.getElementById('ovGc');
    dom.ovNpcs = document.getElementById('ovNpcs');
    dom.ovGround = document.getElementById('ovGround');
    dom.ovAccounts = document.getElementById('ovAccounts');
    dom.ovLastError = document.getElementById('ovLastError');

    dom.playerFilter = document.getElementById('playerFilter');
    dom.playersRefresh = document.getElementById('playersRefresh');
    dom.playersBody = document.getElementById('playersBody');
    dom.playersEmpty = document.getElementById('playersEmpty');
    dom.playerCount = document.getElementById('playerCount');
    dom.navPlayerCount = document.getElementById('navPlayerCount');

    dom.consoleOut = document.getElementById('consoleOut');
    dom.consoleForm = document.getElementById('consoleForm');
    dom.consoleInput = document.getElementById('consoleInput');
    dom.consoleClear = document.getElementById('consoleClear');
    dom.broadcastForm = document.getElementById('broadcastForm');
    dom.broadcastInput = document.getElementById('broadcastInput');

    dom.logOut = document.getElementById('logOut');
    dom.logLevels = document.getElementById('logLevels');
    dom.logAutoScroll = document.getElementById('logAutoScroll');
    dom.logClear = document.getElementById('logClear');
    dom.logCount = document.getElementById('logCount');
    dom.logStatus = document.getElementById('logStatus');

    dom.configSearch = document.getElementById('configSearch');
    dom.configRefresh = document.getElementById('configRefresh');
    dom.configSections = document.getElementById('configSections');
    dom.configEmpty = document.getElementById('configEmpty');
    dom.configMeta = document.getElementById('configMeta');

    dom.modal = document.getElementById('modal');
    dom.modalTitle = document.getElementById('modalTitle');
    dom.modalBody = document.getElementById('modalBody');
    dom.modalClose = document.getElementById('modalClose');
    dom.modalCancel = document.getElementById('modalCancel');
    dom.modalConfirm = document.getElementById('modalConfirm');

    dom.toasts = document.getElementById('toasts');
  }

  function onNavKey(e) {
    var key = e.keyCode;
    if (key !== 37 && key !== 38 && key !== 39 && key !== 40 && key !== 36 && key !== 35) {
      return;
    }

    var items = [];
    for (var i = 0; i < NAV_ORDER.length; i++) {
      items.push(document.getElementById('nav-' + NAV_ORDER[i]));
    }

    var index = -1;
    for (var j = 0; j < items.length; j++) {
      if (items[j] === document.activeElement) { index = j; break; }
    }
    if (index === -1) { return; }

    e.preventDefault();

    if (key === 36) {
      index = 0;
    } else if (key === 35) {
      index = items.length - 1;
    } else if (key === 37 || key === 38) {
      index = (index - 1 + items.length) % items.length;
    } else {
      index = (index + 1) % items.length;
    }

    items[index].focus();
    switchTab(items[index].getAttribute('data-tab'));
  }

  function bindEvents() {
    dom.loginForm.addEventListener('submit', onLoginSubmit);
    dom.tokenInput.addEventListener('keydown', function (e) {
      if (e.keyCode !== 13) { return; }
      e.preventDefault();
      if (dom.loginSubmit.disabled) { return; }
      onLoginSubmit({ preventDefault: function () { /* handled */ } });
    });

    dom.signOutBtn.addEventListener('click', signOut);

    var themeButtons = document.querySelectorAll('[data-theme-set]');
    for (var t = 0; t < themeButtons.length; t++) {
      (function (btn) {
        btn.addEventListener('click', function () {
          applyTheme(btn.getAttribute('data-theme-set'), true);
        });
      })(themeButtons[t]);
    }

    var navButtons = dom.sidebar.querySelectorAll('.nav-item');
    for (var n = 0; n < navButtons.length; n++) {
      (function (btn) {
        btn.addEventListener('click', function () {
          switchTab(btn.getAttribute('data-tab'));
        });
      })(navButtons[n]);
    }
    dom.sidebar.addEventListener('keydown', onNavKey);

    dom.overviewRefresh.addEventListener('click', loadOverview);
    dom.playersRefresh.addEventListener('click', loadPlayers);
    dom.configRefresh.addEventListener('click', function () { loadConfig(true); });

    dom.playerFilter.addEventListener('input', function () {
      state.playerFilter = dom.playerFilter.value;
      renderPlayers();
    });
    dom.playersBody.addEventListener('click', onPlayersClick);

    dom.consoleForm.addEventListener('submit', onConsoleSubmit);
    dom.consoleInput.addEventListener('keydown', onConsoleKey);
    dom.consoleClear.addEventListener('click', function () {
      dom.consoleOut.innerHTML = '';
      dom.consoleInput.focus();
    });

    dom.broadcastForm.addEventListener('submit', onBroadcastSubmit);
    dom.broadcastInput.addEventListener('keydown', onBroadcastKey);

    dom.logLevels.addEventListener('click', onLogLevelClick);
    dom.logAutoScroll.addEventListener('change', onAutoScrollChange);
    dom.logClear.addEventListener('click', clearLogs);
    dom.logOut.addEventListener('scroll', onLogScroll);

    dom.configSearch.addEventListener('input', function () {
      if (state.config) { renderConfig(); }
    });

    dom.modalClose.addEventListener('click', closeModal);
    dom.modalCancel.addEventListener('click', closeModal);
    dom.modalConfirm.addEventListener('click', onModalConfirm);
    dom.modal.addEventListener('click', function (e) {
      if (e.target === dom.modal) { closeModal(); }
    });

    document.addEventListener('keydown', onGlobalKey);
    document.addEventListener('visibilitychange', onVisibilityChange);
    document.addEventListener('webkitvisibilitychange', onVisibilityChange);
  }

  function init() {
    collectDom();
    applyTheme(storageGet(THEME_KEY) || 'light', false);
    bindEvents();
    syncTabs();
    setConn('off');
    bootAuth();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
