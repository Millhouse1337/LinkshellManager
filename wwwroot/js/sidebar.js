(function () {
  'use strict';

  function toggleGroup(btn) {
    const targetSel = btn.getAttribute('data-target');
    if (!targetSel) return;
    const sub = document.querySelector(targetSel);
    if (!sub) return;
    btn.classList.toggle('expanded');
  }

  function closeSwitcher() {
    document.querySelectorAll('[data-ls-menu]').forEach(function (menu) {
      menu.hidden = true;
    });
    document.querySelectorAll('[data-ls-toggle]').forEach(function (btn) {
      btn.setAttribute('aria-expanded', 'false');
    });
  }

  document.addEventListener('click', function (evt) {
    // Linkshell switcher: toggle the dropdown when its header button is clicked.
    const toggle = evt.target.closest('[data-ls-toggle]');
    if (toggle) {
      evt.preventDefault();
      const switcher = toggle.closest('[data-ls-switcher]');
      const menu = switcher ? switcher.querySelector('[data-ls-menu]') : null;
      const willOpen = menu ? menu.hidden : false;
      closeSwitcher();
      if (menu && willOpen) {
        menu.hidden = false;
        toggle.setAttribute('aria-expanded', 'true');
      }
      return;
    }

    // A click anywhere outside an open switcher closes it. Clicks on the menu's
    // own switch buttons fall through and submit their forms normally.
    if (!evt.target.closest('[data-ls-switcher]')) {
      closeSwitcher();
    }

    const btn = evt.target.closest('.nav-item.nav-group');
    if (!btn) return;
    evt.preventDefault();
    toggleGroup(btn);
  });

  document.addEventListener('keydown', function (evt) {
    if (evt.key === 'Escape') closeSwitcher();
  });
})();
