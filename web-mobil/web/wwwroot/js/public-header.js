/* NSX public header interactions - deferred, one request */
(() => {
  const nsxEnglish = (document.documentElement.lang || '').toLowerCase().startsWith('en');
  const header = document.querySelector('.public-site-header');
  if (!header || header.dataset.nsxHeaderReady === 'true') return;
  header.dataset.nsxHeaderReady = 'true';

  const account = header.querySelector('[data-account-menu]');
  if (account) {
    const button = account.querySelector('.account-menu-button');
    const dropdown = account.querySelector('.account-dropdown');
    if (button && dropdown) {
      const setOpen = open => {
        account.classList.toggle('is-open', open);
        button.setAttribute('aria-expanded', String(open));
      };
      button.addEventListener('click', event => {
        event.stopPropagation();
        setOpen(!account.classList.contains('is-open'));
      });
      dropdown.addEventListener('click', event => {
        if (event.target instanceof Element && event.target.closest('a')) setOpen(false);
      });
      document.addEventListener('click', event => {
        if (event.target instanceof Node && !account.contains(event.target)) setOpen(false);
      });
      document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && account.classList.contains('is-open')) {
          setOpen(false);
          button.focus();
        }
      });
    }
  }

  const products = header.querySelector('[data-products-menu]');
  if (products) {
    const trigger = products.querySelector('.nav-products-trigger');
    const panel = products.querySelector('.nav-products-panel');
    if (trigger && panel) {
      const setOpen = open => {
        products.classList.toggle('is-open', open);
        trigger.setAttribute('aria-expanded', String(open));
      };
      trigger.addEventListener('click', event => {
        event.stopPropagation();
        setOpen(!products.classList.contains('is-open'));
      });
      panel.addEventListener('click', event => {
        if (event.target instanceof Element && event.target.closest('a')) setOpen(false);
      });
      document.addEventListener('click', event => {
        if (event.target instanceof Node && !products.contains(event.target)) setOpen(false);
      });
      document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && products.classList.contains('is-open')) {
          setOpen(false);
          trigger.focus();
        }
      });
    }
  }

  const toggle = header.querySelector('.menu-toggle[aria-controls="public-main-menu"]');
  const menu = document.getElementById('public-main-menu');
  if (toggle && menu) {
    const setOpen = open => {
      menu.classList.toggle('is-open', open);
      toggle.classList.toggle('is-open', open);
      toggle.setAttribute('aria-expanded', String(open));
      toggle.setAttribute('aria-label', open ? (nsxEnglish ? 'Close menu' : 'Menüyü kapat') : (nsxEnglish ? 'Open menu' : 'Menüyü aç'));
    };
    toggle.addEventListener('click', event => {
      event.stopPropagation();
      setOpen(!menu.classList.contains('is-open'));
    });
    menu.addEventListener('click', event => {
      if (event.target instanceof Element && event.target.closest('a')) setOpen(false);
    });
    document.addEventListener('click', event => {
      if (!(event.target instanceof Node)) return;
      if (!menu.contains(event.target) && !toggle.contains(event.target)) setOpen(false);
    });
    document.addEventListener('keydown', event => {
      if (event.key === 'Escape' && menu.classList.contains('is-open')) {
        setOpen(false);
        toggle.focus();
      }
    });
    addEventListener('resize', () => {
      if (innerWidth > 980) setOpen(false);
    }, { passive: true });
  }
})();
