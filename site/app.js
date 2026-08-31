/* MIT License - Copyright (c) fintonlabs.com */

(function () {
  'use strict';

  var root = document.documentElement;
  var STORE = 'emberline-theme';

  /* --- Theme -------------------------------------------------------------
     Three states, cycled in the same order the application uses:
     system -> light -> dark -> system.
     -------------------------------------------------------------------- */
  var toggle = document.getElementById('theme-toggle');

  function stored() {
    try { return localStorage.getItem(STORE); } catch (e) { return null; }
  }

  function describe(mode) {
    var next = mode === null ? 'light' : mode === 'light' ? 'dark' : 'system';
    var now = mode === null ? 'system' : mode;
    return 'Colour theme: ' + now + '. Switch to ' + next + '.';
  }

  function apply(mode) {
    if (mode === 'light' || mode === 'dark') root.setAttribute('data-theme', mode);
    else root.removeAttribute('data-theme');

    try {
      if (mode) localStorage.setItem(STORE, mode);
      else localStorage.removeItem(STORE);
    } catch (e) {}

    if (toggle) {
      toggle.setAttribute('aria-label', describe(mode));
      toggle.setAttribute('title', describe(mode));
    }
  }

  apply(stored());

  if (toggle) {
    toggle.addEventListener('click', function () {
      var mode = stored();
      apply(mode === null ? 'light' : mode === 'light' ? 'dark' : null);
    });
  }

  /* --- Raster-pass reveal ------------------------------------------------
     The machine engraves a bitmap by sweeping the head down the work line by
     line. Sections arrive the same way: a clip-path opening top to bottom with
     a lit edge riding the boundary. The edge has to know how far to travel, so
     each element publishes its own height as a custom property.
     -------------------------------------------------------------------- */
  var revealables = Array.prototype.slice.call(document.querySelectorAll('.reveal'));

  var reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  if (!('IntersectionObserver' in window) || reduced) {
    revealables.forEach(function (el) { el.classList.add('is-in'); });
    return;
  }

  // Stagger by position among reveal siblings, so a group of cards cascades
  // while an isolated block arrives on its own.
  revealables.forEach(function (el) {
    var siblings = Array.prototype.filter.call(el.parentNode.children, function (c) {
      return c.classList && c.classList.contains('reveal');
    });
    var index = siblings.indexOf(el);
    el.style.setProperty('--stagger', (index > 0 ? index * 70 : 0) + 'ms');
  });

  var observer = new IntersectionObserver(function (entries) {
    entries.forEach(function (entry) {
      if (!entry.isIntersecting) return;
      var el = entry.target;
      el.style.setProperty('--reveal-h', el.offsetHeight + 'px');
      el.classList.add('is-in');
      observer.unobserve(el);
    });
  }, { rootMargin: '0px 0px -12% 0px', threshold: 0.08 });

  revealables.forEach(function (el) { observer.observe(el); });

  /* --- Info dialog ------------------------------------------------------- */
  var dialog = document.getElementById('info-dialog');
  var open = document.getElementById('info-open');
  var close = document.getElementById('info-close');

  if (dialog && open) {
    open.addEventListener('click', function () {
      if (typeof dialog.showModal === 'function') dialog.showModal();
      else dialog.setAttribute('open', '');
    });

    if (close) {
      close.addEventListener('click', function () { dialog.close(); });
    }

    // Backdrop click: the dialog element itself covers the whole viewport, so a
    // press landing outside the panel's box is a backdrop press.
    dialog.addEventListener('click', function (event) {
      var box = dialog.getBoundingClientRect();
      var outside = event.clientX < box.left || event.clientX > box.right ||
                    event.clientY < box.top  || event.clientY > box.bottom;
      if (outside) dialog.close();
    });
  }
})();
