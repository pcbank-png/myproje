(() => {
  const body = document.body;
  if (!body || body.dataset.nsxProV2 === 'ready') return;
  body.dataset.nsxProV2 = 'ready';
  body.classList.add('nsx-pro-v2-ready');

  const directionClass = symbol => symbol === '↗' ? 'is-up' : symbol === '←' ? 'is-left' : '';
  const makeDirectionInner = symbol => {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    svg.setAttribute('stroke-width', '2.8');
    svg.setAttribute('stroke-linecap', 'round');
    svg.setAttribute('stroke-linejoin', 'round');
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', symbol === '↗' ? 'M7.5 16.5 16.5 7.5M9 7.5h7.5V15' : symbol === '←' ? 'm15 5.5-6.5 6.5 6.5 6.5' : 'm9 5.5 6.5 6.5L9 18.5');
    svg.appendChild(path);
    return svg;
  };

  const turnIntoDirectionIcon = (element, symbol) => {
    if (!(element instanceof Element) || element.dataset.nsxDirectionIcon === 'true') return;
    element.classList.remove('bi', 'bi-arrow-right', 'bi-arrow-up-right');
    element.classList.add('nsx-dir-icon');
    const variant = directionClass(symbol);
    if (variant) element.classList.add(variant);
    if (element.closest('.account-dropdown,.nav-products-panel,.detail-faq-list,.nsx-blog-sidebar-card')) element.classList.add('is-compact');
    element.setAttribute('aria-hidden', 'true');
    element.dataset.nsxDirectionIcon = 'true';
    element.style.setProperty('background', 'linear-gradient(180deg,#fff 0%,#f3f5f7 100%)', 'important');
    element.style.setProperty('color', '#e95b00', 'important');
    element.style.setProperty('border', '1px solid #dfe4e8', 'important');
    element.style.setProperty('box-shadow', '0 6px 14px rgba(12,17,23,.09),inset 0 1px 0 #fff', 'important');
    element.replaceChildren(makeDirectionInner(symbol));
  };

  const upgradeDirectionIcons = (root = document) => {
    const candidates = root.querySelectorAll
      ? root.querySelectorAll('.bi-arrow-right,.bi-arrow-up-right,.cta-action-arrow-glyph,.cta-whatsapp-arrow,a > span:last-child,a > b:last-child,a > i:last-child,button > span:last-child,button > b:last-child,button > i:last-child')
      : [];

    candidates.forEach(element => {
      if (element.matches('.bi-arrow-right')) return turnIntoDirectionIcon(element, '→');
      if (element.matches('.bi-arrow-up-right')) return turnIntoDirectionIcon(element, '↗');
      const symbol = (element.textContent || '').trim();
      if (/^[→↗←]$/.test(symbol) && !element.querySelector('*')) turnIntoDirectionIcon(element, symbol);
    });
  };

  // Header/hero arrows are visible immediately; keep this tiny pass synchronous.
  upgradeDirectionIcons(document);

  const initializeEnhancements = () => {
    const revealSelector = [
      '.nsx-store-section-head', '.section-heading', '.conversion-heading',
      '.home-product-card', '.software-list-page .product-card',
      '.process-card', '.trust-strip', '.final-cta',
      '.detail-media', '.detail-purchase', '.detail-content',
      '.nsx-video-card', '.nsx-video-safe-card', '.nsx-video-description-panel', '.nsx-related-videos',
      '.nsx-blog-card', '.nsx-blog-featured', '.nsx-blog-featured-card', '.nsx-blog-redesign-card', '.nsx-blog-sidebar-card',
      '.nsx-footer-v2__assurance'
    ].join(',');

    const nodes = [...document.querySelectorAll(revealSelector)];
    nodes.forEach((node, index) => {
      node.classList.add('nsx-reveal');
      node.style.setProperty('--nsx-reveal-delay', `${Math.min(index % 4, 3) * 65}ms`);
    });

    if ('IntersectionObserver' in window && !matchMedia('(prefers-reduced-motion: reduce)').matches) {
      const observer = new IntersectionObserver(entries => {
        entries.forEach(entry => {
          if (!entry.isIntersecting) return;
          entry.target.classList.add('is-visible');
          observer.unobserve(entry.target);
        });
      }, { rootMargin: '220px 0px -7% 0px', threshold: .04 });
      nodes.forEach(node => observer.observe(node));
    } else {
      nodes.forEach(node => node.classList.add('is-visible'));
    }

    // One delegated pointer listener replaces one listener per product card.
    let activeCard = null;
    document.addEventListener('pointermove', event => {
      const card = event.target instanceof Element ? event.target.closest('.home-product-card,.software-list-page .product-card') : null;
      if (!card) return;
      activeCard = card;
      const box = activeCard.getBoundingClientRect();
      activeCard.style.setProperty('--nsx-pointer-x', `${event.clientX - box.left}px`);
      activeCard.style.setProperty('--nsx-pointer-y', `${event.clientY - box.top}px`);
    }, { passive: true });
  };

  if ('requestIdleCallback' in window) requestIdleCallback(initializeEnhancements, { timeout: 1800 });
  else setTimeout(initializeEnhancements, 350);
})();
