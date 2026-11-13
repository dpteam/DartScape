/* ==========================================================================
   DartScape — public status site behaviour
   ES5 only. No frameworks, no build step, no fetch/Promise.

   Responsibilities
     * API.get()      — XMLHttpRequest + timeout + JSON guard
     * polling        — api/status every 2s when healthy, backing off to 15s
     * rendering      — every panel, with all server strings escaped
     * events         — append-only, driven by the monotonic `seq`
     * views          — Status / Hiscores / News in one page
     * theme          — light | dark | oled, persisted in localStorage
     * toast + copy   — small quality-of-life helpers

   Contract: docs/WEB-API.md. Every request path is relative so the site keeps
   working when it is mounted under a sub-path.
   ========================================================================== */

(function () {
  'use strict';

  /* ------------------------------------------------------------------------
     Constants
     ------------------------------------------------------------------------ */

  var POLL_MIN = 2000;          /* never poll faster than this */
  var POLL_MAX = 15000;         /* backoff ceiling */
  var POLL_BACKOFF = 1.7;       /* multiplier per consecutive failure */
  var STATUS_TIMEOUT = 8000;
  var OTHER_TIMEOUT = 10000;
  var MAX_EVENT_NODES = 40;     /* keep the DOM bounded */

  var THEME_KEY = 'dartscape.theme';
  var THEMES = ['light', 'dark', 'oled'];
  var THEME_COLORS = {
    light: '#45688e', dark: '#22374f', oled: '#000000'
  };

  var MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
                'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

  var EVENT_KINDS = {
    levelup: 1, login: 1, logout: 1, death: 1,
    drop: 1, trade: 1, quest: 1
  };

  var DEFAULT_ADDRESS = 'rune.dartpower.xyz';
  var DEFAULT_PORT = 43594;

  /* ------------------------------------------------------------------------
     State
     ------------------------------------------------------------------------ */

  var state = {
    fails: 0,
    timer: null,
    inflight: false,
    errorShown: false,
    lastPollAt: 0,
    polls: 0,

    lastSeq: 0,
    playerSig: null,

    view: 'status',
    skill: 'woodcutting',
    pendingSkill: null,
    hiscoreLoading: false,
    hiscoreLoaded: false,
    newsLoading: false,
    newsLoaded: false,

    address: DEFAULT_ADDRESS,
    port: DEFAULT_PORT
  };

  var toastTimer = null;

  /* ------------------------------------------------------------------------
     Tiny DOM / formatting helpers
     ------------------------------------------------------------------------ */

  function byId(id) {
    return document.getElementById(id);
  }

  function qsa(selector) {
    if (!document.querySelectorAll) { return []; }
    return document.querySelectorAll(selector);
  }

  /* Escape every server-provided string before it touches innerHTML. */
  function esc(value) {
    if (value === null || value === undefined) { return ''; }
    return String(value)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function setText(el, value) {
    if (!el) { return; }
    var text = value === null || value === undefined ? '' : String(value);
    if ('textContent' in el) { el.textContent = text; } else { el.innerText = text; }
  }

  function setHTML(el, html) {
    if (el) { el.innerHTML = html; }
  }

  function hasClass(el, name) {
    if (!el || !el.className || typeof el.className !== 'string') { return false; }
    return (' ' + el.className + ' ').indexOf(' ' + name + ' ') !== -1;
  }

  function addClass(el, name) {
    if (!el || hasClass(el, name)) { return; }
    el.className = el.className ? el.className + ' ' + name : name;
  }

  function removeClass(el, name) {
    if (!el || !el.className || typeof el.className !== 'string') { return; }
    var parts = el.className.split(/\s+/);
    var out = [];
    for (var i = 0; i < parts.length; i++) {
      if (parts[i] && parts[i] !== name) { out.push(parts[i]); }
    }
    el.className = out.join(' ');
  }

  function toNum(value) {
    if (value === null || value === undefined || value === '') { return null; }
    var n = Number(value);
    return isFinite(n) ? n : null;
  }

  function fmtInt(value) {
    var n = toNum(value);
    if (n === null) { return '—'; }
    return String(Math.round(n)).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  }

  function pad2(n) {
    return n < 10 ? '0' + n : String(n);
  }

  /* "13 Nov, 10:12" — deterministic, no locale dependence. */
  function fmtClock(unixSec) {
    var s = toNum(unixSec);
    if (s === null || s <= 0) { return '—'; }
    var d = new Date(s * 1000);
    return d.getDate() + ' ' + MONTHS[d.getMonth()] + ', ' +
      pad2(d.getHours()) + ':' + pad2(d.getMinutes());
  }

  /* "42s", "18m", "3h 34m", "2d 4h" */
  function fmtUptime(seconds) {
    var s = toNum(seconds);
    if (s === null || s < 0) { return '—'; }
    s = Math.floor(s);
    var d = Math.floor(s / 86400);
    var h = Math.floor((s % 86400) / 3600);
    var m = Math.floor((s % 3600) / 60);
    if (d > 0) { return d + 'd ' + h + 'h'; }
    if (h > 0) { return h + 'h ' + m + 'm'; }
    if (m > 0) { return m + 'm'; }
    return s + 's';
  }

  function fmtDecimal(value, places) {
    var n = toNum(value);
    if (n === null) { return '—'; }
    return n.toFixed(places);
  }

  function kindClass(kind) {
    var k = String(kind || '').toLowerCase();
    return EVENT_KINDS[k] ? k : 'other';
  }

  function bust(path) {
    return path + (path.indexOf('?') === -1 ? '?' : '&') + '_=' + new Date().getTime();
  }

  /* Walk up from an element to the nearest ancestor carrying `attr`. */
  function closestAttr(node, attr) {
    while (node && node.nodeType === 1 && node !== document.body) {
      if (node.getAttribute && node.getAttribute(attr) !== null) { return node; }
      node = node.parentNode;
    }
    return null;
  }

  /* ------------------------------------------------------------------------
     API — XMLHttpRequest wrapper
     ------------------------------------------------------------------------ */

  var API = {
    get: function (path, cb, timeoutMs) {
      var xhr = new XMLHttpRequest();
      var done = false;
      var timer = null;
      var limit = timeoutMs || STATUS_TIMEOUT;

      function finish(err, data) {
        if (done) { return; }
        done = true;
        if (timer) { clearTimeout(timer); timer = null; }
        cb(err, data);
      }

      try {
        xhr.open('GET', path, true);
      } catch (e) {
        finish('open-failed', null);
        return;
      }

      xhr.onreadystatechange = function () {
        if (xhr.readyState !== 4) { return; }

        var status = 0;
        try { status = xhr.status; } catch (e) { status = 0; }

        if (status >= 200 && status < 300) {
          var data = null;
          try {
            data = JSON.parse(xhr.responseText);
          } catch (e2) {
            finish('bad-json', null);
            return;
          }
          if (!data || data.ok !== true) {
            finish((data && data.error) || 'not-ok', data);
            return;
          }
          finish(null, data);
          return;
        }

        finish('http-' + status, null);
      };

      xhr.onerror = function () { finish('network', null); };

      timer = setTimeout(function () {
        try { xhr.abort(); } catch (e) { /* already gone */ }
        finish('timeout', null);
      }, limit);

      try {
        xhr.send(null);
      } catch (e3) {
        finish('send-failed', null);
      }
    }
  };

  /* ------------------------------------------------------------------------
     Toast — a single reusable element, so only one is ever visible
     ------------------------------------------------------------------------ */

  function toast(message) {
    var el = byId('toast');
    if (!el) { return; }
    el.innerHTML = esc(message);
    addClass(el, 'is-visible');
    if (toastTimer) { clearTimeout(toastTimer); }
    toastTimer = setTimeout(function () {
      removeClass(el, 'is-visible');
      toastTimer = null;
    }, 4200);
  }

  /* ------------------------------------------------------------------------
     Theme
     ------------------------------------------------------------------------ */

  function themeValid(name) {
    return name === 'light' || name === 'dark' || name === 'oled';
  }

  function getTheme() {
    var stored = null;
    try { stored = window.localStorage.getItem(THEME_KEY); } catch (e) { stored = null; }
    if (themeValid(stored)) { return stored; }
    var attr = document.documentElement.getAttribute('data-theme');
    return themeValid(attr) ? attr : 'light';
  }

  function applyTheme(name, persist) {
    if (!themeValid(name)) { name = 'light'; }

    document.documentElement.setAttribute('data-theme', name);

    var meta = document.querySelector('meta[name="theme-color"]');
    if (meta) { meta.setAttribute('content', THEME_COLORS[name]); }

    var buttons = qsa('[data-theme-set]');
    for (var i = 0; i < buttons.length; i++) {
      var on = buttons[i].getAttribute('data-theme-set') === name;
      if (on) { addClass(buttons[i], 'is-active'); } else { removeClass(buttons[i], 'is-active'); }
      buttons[i].setAttribute('aria-pressed', on ? 'true' : 'false');
    }

    if (persist !== false) {
      try { window.localStorage.setItem(THEME_KEY, name); } catch (e) { /* storage off */ }
    }
  }

  function cycleTheme() {
    var current = getTheme();
    var index = 0;
    for (var i = 0; i < THEMES.length; i++) {
      if (THEMES[i] === current) { index = i; }
    }
    applyTheme(THEMES[(index + 1) % THEMES.length], true);
  }

  /* ------------------------------------------------------------------------
     Connection pill + stats strip
     ------------------------------------------------------------------------ */

  function setConnection(mode) {
    var pill = byId('conn-pill');
    if (!pill) { return; }
    removeClass(pill, 'is-online');
    removeClass(pill, 'is-offline');
    removeClass(pill, 'is-unknown');
    addClass(pill, 'is-' + mode);

    if (mode === 'online') {
      setText(byId('conn-text'), 'Online');
      pill.title = 'The world is accepting connections.';
    } else if (mode === 'offline') {
      setText(byId('conn-text'), 'Offline');
      pill.title = 'The status API is not answering. Retrying…';
    } else {
      setText(byId('conn-text'), 'Connecting…');
      pill.title = 'Checking the world…';
    }
  }

  function setStale(stale) {
    var strip = document.querySelector('.statstrip');
    if (!strip) { return; }
    if (stale) { addClass(strip, 'is-stale'); } else { removeClass(strip, 'is-stale'); }
  }

  function renderStats(data) {
    var world = data.world || {};

    var online = toNum(world.playersOnline);
    var max = toNum(world.playersMax);

    setText(byId('stat-players'), online === null ? '—' : fmtInt(online));
    setText(byId('stat-players-note'),
      max ? 'of ' + fmtInt(max) + ' slots' : 'on the world');

    setText(byId('stat-accounts'), fmtInt(world.accountsTotal));
    setText(byId('stat-accounts-note'), 'registered accounts');

    setText(byId('stat-uptime'), fmtUptime(data.uptimeSec));
    setText(byId('stat-uptime-note'),
      data.startedAt ? 'since ' + fmtClock(data.startedAt) : 'since last restart');

    var tps = toNum(world.ticksPerSecond);
    var tickMs = toNum(world.averageTickMs);
    setText(byId('stat-tick'), tps === null ? '—' : fmtDecimal(tps, 2) + ' tps');
    setText(byId('stat-tick-note'),
      tickMs === null || tickMs <= 0
        ? 'game loop speed'
        : fmtDecimal(tickMs, 1) + ' ms average tick');
  }

  /* ------------------------------------------------------------------------
     Server status panel
     ------------------------------------------------------------------------ */

  function portText(value, fallback) {
    var n = toNum(value);
    return String(n === null ? fallback : Math.round(n));
  }

  function renderServer(data) {
    var world = data.world || {};
    var server = data.server || {};
    var online = server.online !== false;
    var relayOnline = server.relayOnline !== false;

    var region = world.region
      ? '<span class="tag tag--muted">' + esc(world.region) + '</span>'
      : '';

    var version = esc(data.version || '—');
    if (data.revision !== null && data.revision !== undefined) {
      version += ' <span class="muted">rev ' + esc(data.revision) + '</span>';
    }

    var rows = [
      ['World', esc(world.name || data.name || 'DartScape') + region],
      ['Status',
        '<span class="dot ' + (online ? 'dot--ok' : 'dot--bad') + '"></span>' +
        (online ? 'Online' : 'Offline')],
      ['Version', version],
      ['Game port', '<span class="mono">' + esc(portText(server.gamePort, DEFAULT_PORT)) + '</span>'],
      ['Web port', '<span class="mono">' + esc(portText(server.webPort, 43595)) + '</span>'],
      ['Relay',
        '<span class="mono">' + esc(portText(server.relayPort, 43597)) + '</span>' +
        ' <span class="dot ' + (relayOnline ? 'dot--ok' : 'dot--warn') + '"></span>' +
        (relayOnline ? 'Online' : 'Offline')],
      ['Access', world.members ? 'Members' : 'Free to play'],
      ['Last restart', esc(server.lastRestartReason || 'unknown')]
    ];
    var html = '';
    for (var i = 0; i < rows.length; i++) {
      html += '<div class="kv-row">' +
        '<span class="kv-key">' + rows[i][0] + '</span>' +
        '<span class="kv-val">' + rows[i][1] + '</span>' +
        '</div>';
    }
    setHTML(byId('server-kv'), html);

    setText(byId('server-note'), online ? 'live' : 'offline');

    var navRegion = byId('nav-region');
    if (navRegion) { setText(navRegion, String(world.region || 'EU').toUpperCase()); }

    if (server.publicAddress) {
      setConnect(server.publicAddress, server.gamePort);
    }
  }

  /* ------------------------------------------------------------------------
     Online players
     ------------------------------------------------------------------------ */

  function renderPlayers(list, world) {
    list = list || [];

    var note = byId('players-note');
    if (note) {
      var online = toNum(world.playersOnline);
      setText(note, fmtInt(online === null ? list.length : online) + ' online');
    }

    /* Skip the table rebuild when names and levels are unchanged. */
    var sigParts = [];
    for (var s = 0; s < list.length; s++) {
      var row = list[s] || {};
      sigParts.push([row.name, row.combat, row.totalLevel, row.world, row.privilege].join('|'));
    }
    var signature = sigParts.join('~');
    if (signature === state.playerSig) { return; }
    state.playerSig = signature;

    var body = byId('players-body');
    if (!body) { return; }

    if (!list.length) {
      setHTML(body, '<tr><td colspan="5" class="empty">Nobody is online right now.</td></tr>');
      return;
    }

    var html = '';
    for (var i = 0; i < list.length; i++) {
      var p = list[i] || {};
      var staff = toNum(p.privilege) > 0
        ? '<span class="staff-star" title="Staff member">&#9733;</span>'
        : '';
      html += '<tr>' +
        '<td class="rank">' + (i + 1) + '</td>' +
        '<td class="player-name">' + esc(p.name || 'Unknown') + staff + '</td>' +
        '<td class="num">' + fmtInt(p.combat) + '</td>' +
        '<td class="num">' + fmtInt(p.totalLevel) + '</td>' +
        '<td class="num">' + fmtInt(p.world) + '</td>' +
        '</tr>';
    }
    setHTML(body, html);
  }

  /* ------------------------------------------------------------------------
     World events — append only, keyed on the monotonic `seq`
     ------------------------------------------------------------------------ */

  function clearEventPlaceholders(list) {
    var placeholders = list.getElementsByClassName
      ? list.getElementsByClassName('empty')
      : [];
    while (placeholders.length > 0) {
      list.removeChild(placeholders[0]);
    }
  }

  function renderEvents(events) {
    events = events || [];

    var list = byId('event-list');
    if (!list) { return; }

    var maxSeq = 0;
    var i;
    for (i = 0; i < events.length; i++) {
      var seqValue = toNum((events[i] || {}).seq);
      if (seqValue !== null && seqValue > maxSeq) { maxSeq = seqValue; }
    }

    /* If seq went backwards the world restarted: rebuild from scratch. */
    if (events.length && maxSeq < state.lastSeq) {
      state.lastSeq = 0;
      setHTML(list, '');
    }

    /* An empty feed after we have rendered events means a fresh world (or a
       restart that has not logged anything yet): drop the stale entries. */
    if (!events.length) {
      if (state.lastSeq > 0) {
        state.lastSeq = 0;
        setHTML(list, '<li class="empty">No world events yet.</li>');
      } else if (!list.firstChild) {
        setHTML(list, '<li class="empty">No world events yet.</li>');
      }
      return;
    }

    var added = 0;
    for (i = 0; i < events.length; i++) {
      var ev = events[i] || {};
      var seq = toNum(ev.seq);
      if (seq !== null && seq <= state.lastSeq) { continue; }

      var li = document.createElement('li');
      li.className = 'event event--' + kindClass(ev.kind);
      li.innerHTML =
        '<span class="event-text">' + esc(ev.text || '') + '</span>' +
        '<span class="event-time">' + esc(fmtClock(ev.at)) + '</span>';
      list.appendChild(li);

      if (seq !== null && seq > state.lastSeq) { state.lastSeq = seq; }
      added++;
    }

    if (added > 0) {
      clearEventPlaceholders(list);
      while (list.childNodes.length > MAX_EVENT_NODES) {
        list.removeChild(list.firstChild);
      }
    }
  }

  /* ------------------------------------------------------------------------
     Top skills
     ------------------------------------------------------------------------ */

  function renderSkills(totals) {
    var box = byId('skills-list');
    if (!box) { return; }

    totals = totals || [];
    if (!totals.length) {
      setHTML(box, '<p class="empty">No skill totals yet.</p>');
      return;
    }

    var rows = [];
    for (var i = 0; i < totals.length; i++) {
      var item = totals[i] || {};
      rows.push({
        skill: String(item.skill || 'Unknown'),
        total: toNum(item.total) === null ? 0 : toNum(item.total)
      });
    }

    rows.sort(function (a, b) { return b.total - a.total; });
    if (rows.length > 6) { rows = rows.slice(0, 6); }

    var max = rows[0].total > 0 ? rows[0].total : 0;
    var html = '';
    for (var j = 0; j < rows.length; j++) {
      /* A zero total gets a genuinely empty track, not a token sliver. */
      var pct = max > 0 && rows[j].total > 0
        ? Math.max(4, Math.round((rows[j].total / max) * 100))
        : 0;
      html += '<div class="skill-row">' +
        '<div class="skill-top">' +
          '<span class="skill-name">' + esc(rows[j].skill) + '</span>' +
          '<span class="skill-total">' + fmtInt(rows[j].total) + '</span>' +
        '</div>' +
        '<div class="skill-bar"><span style="width:' + pct + '%"></span></div>' +
        '</div>';
    }
    setHTML(box, html);
  }

  /* ------------------------------------------------------------------------
     News
     ------------------------------------------------------------------------ */

  function renderNewsList(items, targetId, limit, linkTitles) {
    var box = byId(targetId);
    if (!box) { return; }

    items = items || [];
    if (!items.length) {
      setHTML(box, '<p class="empty">No news has been posted yet.</p>');
      return;
    }

    var count = limit && items.length > limit ? limit : items.length;
    var html = '';
    for (var i = 0; i < count; i++) {
      var item = items[i] || {};
      var meta = esc(fmtClock(item.at));
      if (item.author) { meta += ' <span class="foot-sep">·</span> ' + esc(item.author); }

      var title = esc(item.title || 'Untitled');
      if (linkTitles) {
        title = '<a href="#news" data-view="news">' + title + '</a>';
      }

      html += '<article class="news-item">' +
        '<h3>' + title + '</h3>' +
        '<p class="news-meta">' + meta + '</p>' +
        '<p class="news-body">' + esc(item.body || '').replace(/\n/g, '<br>') + '</p>' +
        '</article>';
    }
    setHTML(box, html);
  }

  function loadNews() {
    if (state.newsLoading) { return; }
    state.newsLoading = true;

    API.get(bust('api/news'), function (err, data) {
      state.newsLoading = false;

      if (err) {
        if (!state.newsLoaded) {
          var message = '<p class="empty">News is unavailable right now.</p>';
          setHTML(byId('news-mini'), message);
          setHTML(byId('news-full'), message);
        }
        return;
      }

      state.newsLoaded = true;
      var items = data.items || [];
      renderNewsList(items, 'news-mini', 3, true);
      renderNewsList(items, 'news-full', 0, false);
    }, OTHER_TIMEOUT);
  }

  /* ------------------------------------------------------------------------
     Hiscores
     ------------------------------------------------------------------------ */

  function skillLabel(skill) {
    var select = byId('skill-select');
    if (select && select.options) {
      for (var i = 0; i < select.options.length; i++) {
        if (select.options[i].value === skill) { return select.options[i].text; }
      }
    }
    return skill;
  }

  function renderHiscore(entries, skill) {
    var body = byId('hiscore-body');
    if (!body) { return; }

    entries = entries || [];
    if (!entries.length) {
      setHTML(body, '<tr><td colspan="4" class="empty">No entries yet — nobody has trained ' +
        esc(skillLabel(skill)) + '.</td></tr>');
      return;
    }

    var html = '';
    for (var i = 0; i < entries.length; i++) {
      var e = entries[i] || {};
      var level = toNum(e.level);
      var levelClass = level !== null && level >= 99 ? 'num level-max' : 'num';
      html += '<tr>' +
        '<td class="rank">' + fmtInt(e.rank === null || e.rank === undefined ? i + 1 : e.rank) + '</td>' +
        '<td class="player-name">' + esc(e.name || 'Unknown') + '</td>' +
        '<td class="' + levelClass + '">' + fmtInt(e.level) + '</td>' +
        '<td class="num">' + fmtInt(e.xp) + '</td>' +
        '</tr>';
    }
    setHTML(body, html);
  }

  function loadHiscore(skill) {
    /* A request is already in flight: remember the newest choice and re-issue
       when it lands, so quickly switching skills never shows stale rows. */
    if (state.hiscoreLoading) {
      state.pendingSkill = skill;
      setText(byId('hiscore-note'), 'top 25 · ' + skillLabel(skill));
      return;
    }
    state.hiscoreLoading = true;
    state.pendingSkill = null;
    state.skill = skill;

    var body = byId('hiscore-body');
    if (body && !state.hiscoreLoaded) {
      setHTML(body, '<tr><td colspan="4" class="empty">Loading hiscores…</td></tr>');
    }
    setText(byId('hiscore-note'), 'top 25 · ' + skillLabel(skill));

    API.get(bust('api/hiscore?skill=' + encodeURIComponent(skill) + '&limit=25'),
      function (err, data) {
        state.hiscoreLoading = false;

        if (err) {
          setText(byId('hiscore-note'), 'unavailable');
          if (!state.hiscoreLoaded) {
            setHTML(body, '<tr><td colspan="4" class="empty">Hiscores are unavailable right now.</td></tr>');
          }
          if (state.view === 'hiscore') { toast('Hiscores unavailable — try again in a moment.'); }
          state.pendingSkill = null;
          return;
        }

        state.hiscoreLoaded = true;
        renderHiscore(data.entries, skill);

        if (state.pendingSkill && state.pendingSkill !== skill) {
          loadHiscore(state.pendingSkill);
        }
      }, OTHER_TIMEOUT);
  }

  /* ------------------------------------------------------------------------
     Connect panel + clipboard
     ------------------------------------------------------------------------ */

  function setConnect(address, port) {
    state.address = address ? String(address) : DEFAULT_ADDRESS;
    var n = toNum(port);
    state.port = n === null ? DEFAULT_PORT : Math.round(n);

    setText(byId('connect-address'), state.address);
    setText(byId('connect-port'), ':' + state.port);
  }

  /* execCommand first (works over plain http and on old browsers), the async
     clipboard API second. The callback always fires exactly once. */
  function copyText(text, done) {
    var ok = false;
    var area = null;
    var finished = false;

    function finish(result) {
      if (finished) { return; }
      finished = true;
      done(result);
    }

    try {
      area = document.createElement('textarea');
      area.value = text;
      area.setAttribute('readonly', 'readonly');
      area.style.position = 'fixed';
      area.style.top = '0';
      area.style.left = '-9999px';
      document.body.appendChild(area);
      area.select();
      try { area.setSelectionRange(0, area.value.length); } catch (e) { /* iOS quirk */ }
      ok = document.execCommand('copy');
    } catch (e2) {
      ok = false;
    }

    if (area && area.parentNode) { area.parentNode.removeChild(area); }

    if (ok) { finish(true); return; }

    if (navigator.clipboard && navigator.clipboard.writeText) {
      try {
        var promise = navigator.clipboard.writeText(text);
        if (promise && typeof promise.then === 'function') {
          promise.then(function () { finish(true); }, function () { finish(false); });
          return;
        }
        finish(true);
        return;
      } catch (e3) { /* fall through to the failure toast */ }
    }

    finish(false);
  }

  function handleCopy() {
    var text = state.address + ':' + state.port;
    copyText(text, function (ok) {
      if (ok) {
        toast('Copied ' + text + ' to the clipboard.');
      } else {
        toast('Could not copy — the address is ' + text + '.');
      }
    });
  }

  /* ------------------------------------------------------------------------
     View switching (no router library)
     ------------------------------------------------------------------------ */

  function showView(name) {
    if (name !== 'status' && name !== 'hiscore' && name !== 'news') { name = 'status'; }
    state.view = name;

    var views = ['status', 'hiscore', 'news'];
    for (var i = 0; i < views.length; i++) {
      var panel = byId('view-' + views[i]);
      if (!panel) { continue; }
      if (views[i] === name) { removeClass(panel, 'is-hidden'); }
      else { addClass(panel, 'is-hidden'); }
    }

    var links = qsa('.tab, .navlink');
    for (var j = 0; j < links.length; j++) {
      var active = links[j].getAttribute('data-view') === name;
      if (active) {
        addClass(links[j], 'is-active');
        links[j].setAttribute('aria-current', 'page');
      } else {
        removeClass(links[j], 'is-active');
        links[j].removeAttribute('aria-current');
      }
    }

    if (name === 'hiscore') { loadHiscore(state.skill); }
    if (name === 'news') { loadNews(); }

    if (window.history && window.history.replaceState) {
      try { window.history.replaceState(null, '', '#' + name); } catch (e) { /* file:// */ }
    }
  }

  /* ------------------------------------------------------------------------
     Status rendering
     ------------------------------------------------------------------------ */

  function renderStatus(data) {
    var world = data.world || {};
    var server = data.server || {};

    setConnection(server.online === false ? 'offline' : 'online');
    setStale(false);

    renderStats(data);
    renderServer(data);
    renderPlayers(data.players, world);
    renderEvents(data.events);
    renderSkills(data.skillTotals);

    setText(byId('about-logins'), fmtInt(data.logins24h));
    setText(byId('about-rev'), data.revision === null || data.revision === undefined ? '—' : String(data.revision));
    setText(byId('foot-version'), 'v' + (data.version || '—'));
    setText(byId('foot-rev'), 'revision ' +
      (data.revision === null || data.revision === undefined ? '—' : data.revision));
  }

  /* ------------------------------------------------------------------------
     Polling with backoff
     ------------------------------------------------------------------------ */

  function nextDelay() {
    var delay;
    if (state.fails === 0) {
      delay = POLL_MIN;
    } else {
      delay = Math.min(POLL_MAX, Math.round(POLL_MIN * Math.pow(POLL_BACKOFF, state.fails)));
    }
    if (document.hidden) { delay = delay * 3; }
    return Math.max(POLL_MIN, delay);
  }

  function schedulePoll() {
    if (state.timer) { clearTimeout(state.timer); state.timer = null; }
    state.timer = setTimeout(pollStatus, nextDelay());
  }

  function pollStatus(force) {
    /* Hard floor: never issue two status requests inside POLL_MIN. */
    var now = new Date().getTime();
    var since = now - state.lastPollAt;
    if (!force && state.lastPollAt && since < POLL_MIN) {
      if (state.timer) { clearTimeout(state.timer); state.timer = null; }
      state.timer = setTimeout(pollStatus, POLL_MIN - since);
      return;
    }
    if (state.inflight) {
      schedulePoll();
      return;
    }
    state.inflight = true;
    state.lastPollAt = now;

    API.get(bust('api/status'), function (err, data) {
      state.inflight = false;

      if (err || !data) {
        state.fails++;
        setConnection('offline');
        if (state.fails >= 2) { setStale(true); }
        setText(byId('server-note'), 'reconnecting');
        if (!state.errorShown) {
          state.errorShown = true;
          toast('Status unavailable — retrying…');
        }
      } else {
        state.fails = 0;
        state.errorShown = false;
        renderStatus(data);

        /* The mini news panel is cheap; refresh it every ~5 minutes. */
        state.polls++;
        if (state.polls % 150 === 0) { loadNews(); }
      }

      schedulePoll();
    }, STATUS_TIMEOUT);
  }

  /* ------------------------------------------------------------------------
     Wiring
     ------------------------------------------------------------------------ */

  function wireClicks() {
    document.addEventListener('click', function (event) {
      var target = event.target || event.srcElement;

      var viewNode = closestAttr(target, 'data-view');
      if (viewNode) {
        event.preventDefault();
        showView(viewNode.getAttribute('data-view'));
        return;
      }

      if (closestAttr(target, 'data-copy')) {
        event.preventDefault();
        handleCopy();
      }
    });
  }

  function wireTheme() {
    var buttons = qsa('[data-theme-set]');
    for (var i = 0; i < buttons.length; i++) {
      buttons[i].onclick = function () {
        applyTheme(this.getAttribute('data-theme-set'), true);
      };
    }

    /* "t" cycles the theme when the user is not typing. */
    document.addEventListener('keydown', function (event) {
      var key = event.key || String.fromCharCode(event.keyCode || 0);
      if (key !== 't' && key !== 'T') { return; }
      if (event.ctrlKey || event.metaKey || event.altKey) { return; }

      var node = event.target || event.srcElement;
      var tag = node && node.nodeName ? node.nodeName.toUpperCase() : '';
      if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') { return; }

      cycleTheme();
    });
  }

  function wireControls() {
    var play = byId('play-btn');
    if (play) {
      play.onclick = function () {
        var panel = byId('connect-panel');
        if (panel) {
          if (panel.scrollIntoView) { panel.scrollIntoView(); }
          addClass(panel, 'is-pulsing');
          setTimeout(function () { removeClass(panel, 'is-pulsing'); }, 1100);
        }
        toast('Connect to ' + state.address + ':' + state.port + ' from your client.');
      };
    }

    var select = byId('skill-select');
    if (select) {
      select.onchange = function () {
        loadHiscore(this.value);
      };
    }

    var refresh = byId('hiscore-refresh');
    if (refresh) {
      refresh.onclick = function () {
        loadHiscore(state.skill);
      };
    }

    document.addEventListener('visibilitychange', function () {
      if (!document.hidden) {
        if (state.timer) { clearTimeout(state.timer); state.timer = null; }
        pollStatus();
      } else {
        schedulePoll();
      }
    });
  }

  /* ------------------------------------------------------------------------
     Boot
     ------------------------------------------------------------------------ */

  function init() {
    applyTheme(getTheme(), false);
    wireTheme();
    wireClicks();
    wireControls();

    setConnect(DEFAULT_ADDRESS, DEFAULT_PORT);

    var hash = String(window.location.hash || '').replace('#', '');
    if (hash === 'hiscore' || hash === 'news') { showView(hash); }
    else { showView('status'); }

    loadNews();   /* fills the News panel on the status view */
    pollStatus(true);
  }

  /* Exposed for the admin console and for debugging. */
  window.API = API;
  window.DartScape = {
    getTheme: getTheme,
    applyTheme: applyTheme,
    cycleTheme: cycleTheme,
    refresh: function () { pollStatus(true); },
    render: renderStatus,
    state: state
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
}());
