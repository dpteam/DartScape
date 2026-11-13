/* ---------------------------------------------------------------------------
   Theme bootstrap.

   Loaded synchronously in <head>, before the body is parsed, so the stored
   theme is applied before the first paint. Without this the page paints in the
   default light theme and then flips, which is a visible flash on every load.

   This lives in its own file rather than inline because the Content-Security-
   Policy forbids inline scripts. An inline block would need either
   'unsafe-inline' (which defeats the policy) or a hash in the header (which
   breaks silently the moment this file is edited). A blocking <script src> has
   neither problem and is cached after the first load.
   --------------------------------------------------------------------------- */

(function () {
  var THEMES = ['light', 'dark', 'oled'];
  var COLOURS = {
    light: '#45688e', dark: '#22374f', oled: '#000000'
  };

  var theme;

  try {
    theme = window.localStorage.getItem('dartscape.theme');
  } catch (e) {
    // Private mode or disabled storage: fall through to the default.
    return;
  }

  if (THEMES.indexOf(theme) < 0) {
    return;
  }

  document.documentElement.setAttribute('data-theme', theme);

  var meta = document.querySelector('meta[name="theme-color"]');
  if (meta && COLOURS[theme]) {
    meta.setAttribute('content', COLOURS[theme]);
  }
}());
