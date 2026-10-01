/* NSX Home Runtime V3: header + deferred UI + lightweight presence */
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

(() => {
  const nsxEnglish = (document.documentElement.lang || '').toLowerCase().startsWith('en');
  const body = document.body;
  if (!body || body.dataset.nsxHomeRuntime === 'ready') return;
  body.dataset.nsxHomeRuntime = 'ready';
  body.classList.add('nsx-pro-v2-ready');

  const productGrid = document.querySelector('.home-products-grid');
  const productMoreButton = document.querySelector('[data-home-products-more]');
  if (productGrid && productMoreButton) {
    const productCards = [...productGrid.querySelectorAll('.home-product-card')];
    const mobileProducts = matchMedia('(max-width: 680px)');
    let productsExpanded = false;
    const syncProducts = () => {
      const shouldCollapse = mobileProducts.matches && !productsExpanded && productCards.length > 4;
      productGrid.classList.toggle('is-mobile-collapsed', shouldCollapse);
      productMoreButton.hidden = !shouldCollapse;
      productMoreButton.setAttribute('aria-expanded', String(!shouldCollapse));
      const remaining = Math.max(0, productCards.length - 4);
      const label = productMoreButton.querySelector('strong');
      if (label && remaining) label.textContent = nsxEnglish ? `Show ${remaining} more products` : `${remaining} ürünü daha göster`;
    };
    productMoreButton.addEventListener('click', () => {
      productsExpanded = true;
      syncProducts();
    });
    mobileProducts.addEventListener?.('change', syncProducts);
    syncProducts();
  }

  const footerColumns = [...document.querySelectorAll('.nsx-footer-v2__column')];
  if (footerColumns.length) {
    const mobileFooter = matchMedia('(max-width: 600px)');
    footerColumns.forEach((column, index) => {
      const heading = column.querySelector('h3');
      if (!heading) return;
      heading.setAttribute('role', 'button');
      heading.setAttribute('tabindex', '0');
      heading.setAttribute('aria-expanded', 'false');
      heading.setAttribute('aria-controls', `nsx-footer-column-${index + 1}`);
      column.id = `nsx-footer-column-${index + 1}`;
      const toggle = () => {
        if (!mobileFooter.matches) return;
        const open = column.classList.toggle('is-open');
        heading.setAttribute('aria-expanded', String(open));
      };
      heading.addEventListener('click', toggle);
      heading.addEventListener('keydown', event => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          toggle();
        }
      });
    });
    const syncFooter = () => {
      footerColumns.forEach(column => {
        if (!mobileFooter.matches) column.classList.remove('is-open');
        column.querySelector('h3')?.setAttribute('aria-expanded', mobileFooter.matches ? 'false' : 'true');
      });
    };
    mobileFooter.addEventListener?.('change', syncFooter);
    syncFooter();
  }

  const scrollTopButton = document.querySelector('[data-home-scroll-top]');
  if (scrollTopButton) {
    let scrollFrame = 0;
    const syncScrollTopButton = () => {
      scrollFrame = 0;
      const showAfter = Math.max(440, innerHeight * .58);
      scrollTopButton.classList.toggle('is-visible', scrollY > showAfter);
    };
    addEventListener('scroll', () => {
      if (!scrollFrame) scrollFrame = requestAnimationFrame(syncScrollTopButton);
    }, { passive: true });
    scrollTopButton.addEventListener('click', () => {
      const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;
      scrollTo({ top: 0, behavior: reducedMotion ? 'auto' : 'smooth' });
    });
    syncScrollTopButton();
  }

  const enhanceBelowFold = () => {
    const selector = [
      '.nsx-store-section-head', '.section-heading', '.conversion-heading',
      '.home-product-card', '.process-card', '.trust-strip', '.final-cta',
      '.nsx-footer-v2__assurance'
    ].join(',');
    const nodes = document.querySelectorAll(selector);

    nodes.forEach((node, index) => {
      node.classList.add('nsx-reveal');
      node.style.setProperty('--nsx-reveal-delay', `${Math.min(index % 4, 3) * 65}ms`);
    });

    if ('IntersectionObserver' in window && !matchMedia('(prefers-reduced-motion: reduce)').matches) {
      const observer = new IntersectionObserver(entries => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          entry.target.classList.add('is-visible');
          observer.unobserve(entry.target);
        }
      }, { rootMargin: '220px 0px -7% 0px', threshold: .04 });
      nodes.forEach(node => observer.observe(node));
    } else {
      nodes.forEach(node => node.classList.add('is-visible'));
    }

    if (matchMedia('(hover:hover) and (pointer:fine)').matches) {
      document.addEventListener('pointermove', event => {
        const target = event.target;
        if (!(target instanceof Element)) return;
        const card = target.closest('.home-product-card');
        if (!card) return;
        const box = card.getBoundingClientRect();
        card.style.setProperty('--nsx-pointer-x', `${event.clientX - box.left}px`);
        card.style.setProperty('--nsx-pointer-y', `${event.clientY - box.top}px`);
      }, { passive: true });
    }
  };

  const runIdle = callback => {
    if ('requestIdleCallback' in window) requestIdleCallback(callback, { timeout: 1800 });
    else setTimeout(callback, 400);
  };
  runIdle(enhanceBelowFold);

  const storageKey = 'nsx-online-visitor-id';
  const minimumGapMs = 15000;
  let visitorId = '';
  let lastPingAt = 0;

  const ensureVisitorId = () => {
    if (visitorId) return visitorId;
    try {
      visitorId = localStorage.getItem(storageKey) || '';
      if (!visitorId) {
        visitorId = window.crypto && typeof window.crypto.randomUUID === 'function'
          ? window.crypto.randomUUID()
          : `v-${Date.now()}-${Math.random().toString(16).slice(2)}`;
        localStorage.setItem(storageKey, visitorId);
      }
    } catch {
      visitorId = `v-${Date.now()}-${Math.random().toString(16).slice(2)}`;
    }
    return visitorId;
  };

  const ping = force => {
    if (document.visibilityState === 'hidden') return;
    const now = Date.now();
    if (!force && now - lastPingAt < minimumGapMs) return;
    lastPingAt = now;
    fetch('/presence/ping', {
      method: 'POST', credentials: 'same-origin', cache: 'no-store', keepalive: true,
      headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
      body: JSON.stringify({
        visitorId: ensureVisitorId(),
        pagePath: `${location.pathname}${location.search}`,
        pageTitle: document.title,
        referrer: document.referrer
      })
    }).catch(() => {});
  };

  // Analytics-style presence must never compete with first paint/LCP.
  const startPresence = () => {
    setTimeout(() => ping(true), 15000);
    setInterval(() => ping(false), 45000);
  };
  if (document.readyState === 'complete') startPresence();
  else addEventListener('load', startPresence, { once: true });

  addEventListener('focus', () => ping(false), { passive: true });
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') ping(false);
  }, { passive: true });
})();
