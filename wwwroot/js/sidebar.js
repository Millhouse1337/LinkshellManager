(function () {
  'use strict';

  function toggleGroup(btn) {
    const targetSel = btn.getAttribute('data-target');
    if (!targetSel) return;
    const sub = document.querySelector(targetSel);
    if (!sub) return;
    btn.classList.toggle('expanded');
  }

  document.addEventListener('click', function (evt) {
    const btn = evt.target.closest('.nav-item.nav-group');
    if (!btn) return;
    evt.preventDefault();
    toggleGroup(btn);
  });
})();
