(() => {
    'use strict';

    const switchers = [...document.querySelectorAll('[data-language-switcher]')];

    const closeAll = except => {
        for (const item of switchers) {
            if (item !== except) item.removeAttribute('open');
        }
    };

    document.addEventListener('click', event => {
        for (const item of switchers) {
            if (!item.contains(event.target)) item.removeAttribute('open');
        }
    });

    document.addEventListener('keydown', event => {
        if (event.key !== 'Escape') return;
        for (const item of switchers) item.removeAttribute('open');
    });

    for (const item of switchers) {
        item.addEventListener('toggle', () => {
            if (item.open) closeAll(item);
        });
    }

    const countryCodeFromValue = value => {
        const raw = (value || '').trim();
        if (/^[A-Za-z]{2}$/.test(raw)) return raw.toUpperCase();

        const points = Array.from(raw, char => char.codePointAt(0));
        if (points.length !== 2) return null;
        if (!points.every(point => point >= 0x1F1E6 && point <= 0x1F1FF)) return null;

        return points
            .map(point => String.fromCharCode(65 + point - 0x1F1E6))
            .join('');
    };

    const flagEmojiFromCountryCode = value => {
        const code = countryCodeFromValue(value);
        if (!code) return (value || '').trim();

        return Array.from(code)
            .map(char => String.fromCodePoint(0x1F1E6 + char.charCodeAt(0) - 65))
            .join('');
    };

    const makeFlagImage = (countryCode, width, height) => {
        const img = document.createElement('img');
        img.src = `https://flagcdn.com/${countryCode.toLowerCase()}.svg`;
        img.alt = '';
        img.width = width;
        img.height = height;
        img.loading = 'lazy';
        img.decoding = 'async';
        img.style.display = 'block';
        img.style.width = '100%';
        img.style.height = '100%';
        img.style.objectFit = 'cover';
        img.style.borderRadius = '2px';
        return img;
    };

    const replaceFlagText = (element, width, height) => {
        if (!element || element.querySelector('img')) return;
        const countryCode = countryCodeFromValue(element.textContent);
        if (!countryCode) return;
        element.textContent = '';
        element.appendChild(makeFlagImage(countryCode, width, height));
    };

    document.querySelectorAll('.nsx-language-emoji').forEach(element => replaceFlagText(element, 30, 21));
    document.querySelectorAll('.nsx-lang-flag').forEach(element => {
        element.style.display = 'inline-grid';
        element.style.placeItems = 'center';
        element.style.width = '34px';
        element.style.height = '24px';
        element.style.overflow = 'hidden';
        element.style.borderRadius = '4px';
        replaceFlagText(element, 34, 24);
    });

    document.querySelectorAll('.nsx-admin-language > summary > span[aria-hidden="true"]').forEach(element => {
        element.style.display = 'inline-grid';
        element.style.placeItems = 'center';
        element.style.width = '24px';
        element.style.height = '17px';
        element.style.overflow = 'hidden';
        element.style.borderRadius = '3px';
        replaceFlagText(element, 24, 17);
    });

    document.querySelectorAll('.nsx-admin-language-menu button > span:first-child').forEach(element => {
        element.style.display = 'inline-grid';
        element.style.placeItems = 'center';
        element.style.width = '24px';
        element.style.height = '17px';
        element.style.overflow = 'hidden';
        element.style.borderRadius = '3px';
        replaceFlagText(element, 24, 17);
    });

    document.addEventListener('submit', event => {
        const input = event.target?.querySelector?.('input[name="flagEmoji"]');
        if (!input) return;
        input.value = flagEmojiFromCountryCode(input.value);
    }, true);
})();
