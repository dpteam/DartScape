/* ---------------------------------------------------------------------------
   DartScape launcher script.

   Runs inside MSHTML, so this is ES3 with a few ES5 conveniences that IE8+
   understands: no arrow functions, no let/const, no template literals, no
   Promise, no fetch. XMLHttpRequest only.

   Host bridge
   -----------
   Calling into the host is done by navigating to a private scheme and having the
   host cancel the navigation:

       Launcher.send('verb', 'payload')  ->  location = 'dartscape://verb'
                                             host cancels, reads payload via
                                             dartscapeTakePayload(), dispatches
       host -> dartscapeReceive(verb, payload)

   The payload has to be stashed in a global because the URL cannot carry
   arbitrary text safely, and the host cannot call a JS function that returns a
   value before the navigation is cancelled.
   --------------------------------------------------------------------------- */

var Launcher = (function () {

  var BRIDGE = 'dartscape://';

  var state = {
    theme: 'light',
    worlds: [],
    busy: false
  };

  /* ---------------------------------------------------------------- bridge */

  function send(verb, payload) {
    window.__outbox = (payload === undefined || payload === null) ? '' : String(payload);
    try {
      window.location.href = BRIDGE + verb;
    } catch (e) {
      // Some engines throw on an unknown scheme instead of navigating.
    }
  }

  /* Called by the host immediately after it cancels the bridge navigation. */
  window.dartscapeTakePayload = function () {
    var payload = window.__outbox || '';
    window.__outbox = '';
    return payload;
  };

  /* Called by the host to push state into the page. */
  window.dartscapeReceive = function (verb, payload) {
    var name = String(verb || '').toLowerCase();

    if (name === 'settings') {
      applySettings(parseJson(payload));
      return;
    }

    if (name === 'worlds') {
      state.worlds = parseJson(payload) || [];
      renderWorlds();
      return;
    }

    if (name === 'ping') {
      var result = parseJson(payload);
      showPing(result && result.ok, result && result.ms);
      return;
    }

    if (name === 'status') {
      setStatus(payload, false);
      return;
    }

    if (name === 'error') {
      setStatus(payload, true);
      setBusy(false);
      return;
    }

    if (name === 'launched') {
      setStatus(payload || 'Starting DartScape...', false);
      setBusy(true);
      return;
    }

    if (name === 'theme') {
      setTheme(String(payload || 'light'));
      return;
    }
  };

  /* ---------------------------------------------------------------- helpers */

  function parseJson(text) {
    if (!text) return null;
    try {
      return JSON.parse(text);
    } catch (e) {
      return null;
    }
  }

  function $(id) {
    return document.getElementById(id);
  }

  function setStatus(text, isError) {
    var el = $('status');
    if (!el) return;
    el.innerHTML = escapeHtml(text || '');
    el.className = 'status' + (isError ? ' error' : '');
    $('footStatus').innerHTML = escapeHtml(text || 'Ready');
  }

  function setBusy(busy) {
    state.busy = busy;
    var btn = $('playBtn');
    if (!btn) return;
    btn.className = busy ? 'play disabled' : 'play';
    btn.innerHTML = busy ? 'Launching...' : 'Play';
  }

  function escapeHtml(text) {
    return String(text === undefined || text === null ? '' : text)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function showPing(ok, ms) {
    var pill = $('pingPill');
    if (!pill) return;

    if (ok && ms > 0) {
      pill.className = 'pill ok';
      pill.innerHTML = 'online &middot; ' + ms + ' ms';
    } else {
      pill.className = 'pill bad';
      pill.innerHTML = 'unreachable';
    }
  }

  /* ------------------------------------------------------------------ tabs */

  function showTab(name) {
    var tabs = ['play', 'worlds', 'settings', 'about'];
    for (var i = 0; i < tabs.length; i++) {
      var page = $('page-' + tabs[i]);
      var tab = $('tab-' + tabs[i]);
      var active = tabs[i] === name;

      if (page) page.className = active ? 'page' : 'page hidden';
      if (tab) tab.className = active ? 'tab active' : 'tab';
    }

    if (name === 'worlds' && state.worlds.length === 0) refreshWorlds();
  }

  /* ---------------------------------------------------------------- themes */

  function setTheme(name) {
    if (name !== 'light' && name !== 'dark' && name !== 'oled') name = 'light';
    state.theme = name;
    document.body.className = 'theme-' + name;

    try { localStorage.setItem('dartscape.launcher.theme', name); } catch (e) { }
    send('themeSave', name);
  }

  function cycleTheme() {
    var order = ['light', 'dark', 'oled'];
    var next = order[(indexOf(order, state.theme) + 1) % order.length];
    setTheme(next);
  }

  function indexOf(array, value) {
    for (var i = 0; i < array.length; i++) if (array[i] === value) return i;
    return -1;
  }

  /* -------------------------------------------------------------- settings */

  function applySettings(data) {
    if (!data) return;

    if ($('name')) $('name').value = data.name || '';
    if ($('password')) $('password').value = data.password || '';
    if ($('server')) $('server').value = data.server || '';
    if ($('width')) $('width').value = data.width || 765;
    if ($('height')) $('height').value = data.height || 503;
    if ($('cache')) $('cache').value = data.cache || '';
    if ($('sound')) $('sound').checked = !!data.sound;
    if ($('music')) $('music').checked = !!data.music;

    if ($('version') && data.version) $('version').innerHTML = 'version ' + escapeHtml(data.version);
    if ($('footVersion') && data.version) $('footVersion').innerHTML = 'v' + escapeHtml(data.version);

    setTheme(data.theme || 'light');
    setStatus(data.autoStatus || 'Ready.', false);
  }

  function collectSettings() {
    return {
      name: value('name'),
      password: value('password'),
      server: value('server'),
      width: number('width', 765),
      height: number('height', 503),
      cache: value('cache'),
      sound: checked('sound'),
      music: checked('music'),
      theme: state.theme
    };
  }

  function value(id) {
    var el = $(id);
    return el ? el.value : '';
  }

  function number(id, fallback) {
    var parsed = parseInt(value(id), 10);
    return isNaN(parsed) ? fallback : parsed;
  }

  function checked(id) {
    var el = $(id);
    return el ? !!el.checked : false;
  }

  function saveSettings() {
    send('saveSettings', JSON.stringify(collectSettings()));
    setStatus('Settings saved.', false);
  }

  function openLogs() {
    send('openLogs', '');
  }

  /* ------------------------------------------------------------------ play */

  function play() {
    if (state.busy) return;

    var settings = collectSettings();

    if (!settings.name) {
      setStatus('Enter a username first.', true);
      $('name').focus();
      return;
    }

    if (!settings.password) {
      setStatus('Enter a password first.', true);
      $('password').focus();
      return;
    }

    if (!settings.server) {
      setStatus('Enter a server address first.', true);
      $('server').focus();
      return;
    }

    setStatus('Starting DartScape...', false);
    setBusy(true);
    send('play', JSON.stringify(settings));
  }

  function ping() {
    var settings = collectSettings();
    if (!settings.server) {
      setStatus('Enter a server address first.', true);
      return;
    }

    setStatus('Testing connection...', false);
    var pill = $('pingPill');
    if (pill) { pill.className = 'pill'; pill.innerHTML = 'testing'; }
    send('ping', settings.server);
  }

  function onNameChange() {
    var el = $('name');
    if (!el) return;

    // Mirror the server's name rules live, so a rejected login is never a surprise.
    var cleaned = el.value.replace(/[^A-Za-z0-9 _-]/g, '');
    if (cleaned !== el.value) el.value = cleaned;
    if (el.value.length > 12) el.value = el.value.substring(0, 12);
  }

  /* ---------------------------------------------------------------- worlds */

  function refreshWorlds() {
    setStatus('Querying worlds...', false);
    send('worlds', '');
  }

  function renderWorlds() {
    var container = $('worlds');
    if (!container) return;

    if (!state.worlds || state.worlds.length === 0) {
      container.innerHTML = '<div class="empty">No worlds found. Check the server address.</div>';
      return;
    }

    var html = '';
    for (var i = 0; i < state.worlds.length; i++) {
      var w = state.worlds[i];
      var load = (w.players || 0) + ' / ' + (w.max || 0);

      html += '<div class="row2">' +
        '<button class="wjoin" onclick="Launcher.joinWorld(' + i + ')">Join</button>' +
        '<div class="wname">' + escapeHtml(w.name || 'DartScape') + '</div>' +
        '<div class="wmeta">' + escapeHtml(w.address || '') + ':' + (w.port || 43594) +
        ' &middot; ' + escapeHtml(load) + ' players' +
        ' &middot; ' + escapeHtml(w.region || 'EU') +
        (w.online ? ' &middot; online' : ' &middot; offline') +
        '</div>' +
        '</div>';
    }

    container.innerHTML = html;
  }

  function joinWorld(index) {
    var world = state.worlds[index];
    if (!world) return;

    var address = (world.address || '') + ':' + (world.port || 43594);
    if ($('server')) $('server').value = address;

    showTab('play');
    setStatus('Selected ' + (world.name || 'world') + '.', false);
  }

  /* -------------------------------------------------------------- start-up */

  function init() {
    var stored = null;
    try { stored = localStorage.getItem('dartscape.launcher.theme'); } catch (e) { }

    setTheme(stored || 'light');

    // Ask the host to fill in the saved settings; it answers with dartscapeReceive.
    send('ready', '');

    // Enter anywhere on the play page starts the game.
    document.onkeydown = function (e) {
      var key = e ? (e.which || e.keyCode) : 0;
      if (key === 13 && !$('page-play').className) play();
    };
  }

  return {
    showTab: showTab,
    setTheme: setTheme,
    cycleTheme: cycleTheme,
    saveSettings: saveSettings,
    openLogs: openLogs,
    play: play,
    ping: ping,
    onNameChange: onNameChange,
    refreshWorlds: refreshWorlds,
    joinWorld: joinWorld,
    init: init
  };

})();

Launcher.init();
