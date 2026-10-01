(() => {
  const guide = document.querySelector('[data-install-guide]');
  if (!guide) return;

  const storageKey = 'nsx-install-guide-hidden';
  let autoOpenSuppressed = false;
  try {
    autoOpenSuppressed = localStorage.getItem(storageKey) === 'true';
  } catch { }

  const panel = guide.querySelector('.nsx-guide__panel');
  const remember = guide.querySelector('[data-guide-remember]');
  const countLabels = guide.querySelectorAll('[data-guide-count], [data-guide-count-ring]');
  const ring = guide.querySelector('.nsx-guide__timer i');
  const duration = Math.max(1, Number(guide.dataset.duration) || 10);
  let remaining = duration;
  let timerId = 0;
  let returnFocus = null;

  const render = () => {
    countLabels.forEach(label => { label.textContent = String(remaining); });
    ring?.style.setProperty('--guide-progress', String(remaining / duration));
  };

  const close = () => {
    if (guide.hidden) return;
    clearInterval(timerId);
    if (remember?.checked) {
      try { localStorage.setItem(storageKey, 'true'); } catch { }
    }
    guide.hidden = true;
    document.body.classList.remove('nsx-guide-open');
    returnFocus?.focus?.({ preventScroll: true });
  };

  const open = trigger => {
    clearInterval(timerId);
    returnFocus = trigger instanceof HTMLElement ? trigger : document.activeElement;
    remaining = duration;
    if (remember) remember.checked = false;
    guide.hidden = false;
    document.body.classList.add('nsx-guide-open');
    render();
    requestAnimationFrame(() => panel?.focus({ preventScroll: true }));
    timerId = window.setInterval(() => {
      remaining -= 1;
      render();
      if (remaining <= 0) close();
    }, 1000);
  };

  guide.querySelectorAll('[data-guide-close]').forEach(button => button.addEventListener('click', close));
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && !guide.hidden) close();
  });
  document.querySelectorAll('[data-guide-open]').forEach(button => {
    button.addEventListener('click', () => open(button));
  });

  if (guide.dataset.autoOpen === 'true' && !autoOpenSuppressed) open();
})();
