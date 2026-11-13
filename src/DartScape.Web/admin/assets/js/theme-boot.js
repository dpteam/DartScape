/* ---------------------------------------------------------------------------
   Admin theme bootstrap.

   Runs synchronously in <head> so an operator who prefers the dark theme never
   sees a white flash before app.js takes over.

   External rather than inline because the Content-Security-Policy forbids inline
   scripts; see public/assets/js/theme-boot.js for the full reasoning.
   --------------------------------------------------------------------------- */

(function () {
  var THEMES = ['light', 'dark', 'oled'];

  var theme;

  try {
    theme = window.localStorage.getItem('dartscape.admin.theme');
  } catch (e) {
    return;
  }

  if (THEMES.indexOf(theme) < 0) {
    return;
  }

  document.documentElement.setAttribute('data-theme', theme);
}());
