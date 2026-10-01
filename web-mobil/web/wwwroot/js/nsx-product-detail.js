const nsxProductEnglish = (document.documentElement.lang || '').toLowerCase().startsWith('en');
function nsxChangeProductImage(path) {
    const main = document.getElementById('nsxMainProductImage');
    if (main && path) {
        main.src = path;
    }
}

function nsxSelectLicense(label) {
    document.querySelectorAll('.nsx-pro-license').forEach(function (x) {
        x.classList.remove('active');
    });

    label.classList.add('active');

    const input = label.querySelector('input[type="radio"]');
    if (input) {
        input.checked = true;
    }
}

document.addEventListener('DOMContentLoaded', function () {
    const thumbButtons = Array.from(document.querySelectorAll('.nsx-pro-thumbs button[data-image]'));
    let activeImageIndex = thumbButtons.findIndex(function (button) {
        return button.classList.contains('active');
    });

    if (activeImageIndex < 0) {
        activeImageIndex = 0;
    }

    function selectGalleryImage(index) {
        if (!thumbButtons.length) {
            return;
        }

        if (index < 0) {
            index = thumbButtons.length - 1;
        }

        if (index >= thumbButtons.length) {
            index = 0;
        }

        activeImageIndex = index;

        thumbButtons.forEach(function (button) {
            button.classList.remove('active');
        });

        const selected = thumbButtons[activeImageIndex];
        selected.classList.add('active');
        nsxChangeProductImage(selected.getAttribute('data-image'));

        if (selected.scrollIntoView) {
            selected.scrollIntoView({ behavior: 'smooth', inline: 'center', block: 'nearest' });
        }
    }

    thumbButtons.forEach(function (button, index) {
        button.addEventListener('click', function () {
            selectGalleryImage(index);
        });
    });

    document.querySelectorAll('[data-gallery-dir]').forEach(function (button) {
        button.addEventListener('click', function () {
            const direction = button.getAttribute('data-gallery-dir');
            selectGalleryImage(direction === 'prev' ? activeImageIndex - 1 : activeImageIndex + 1);
        });
    });


    const thumbsWrap = document.querySelector('.nsx-pro-thumbs');
    if (thumbsWrap) {
        thumbsWrap.addEventListener('wheel', function (event) {
            if (Math.abs(event.deltaY) > Math.abs(event.deltaX)) {
                event.preventDefault();
                thumbsWrap.scrollLeft += event.deltaY;
            }
        }, { passive: false });
    }

    document.querySelectorAll('.nsx-pro-tabs button').forEach(function (button) {
        button.addEventListener('click', function () {
            const tab = button.getAttribute('data-tab');

            document.querySelectorAll('.nsx-pro-tabs button').forEach(function (x) {
                x.classList.remove('active');
            });

            document.querySelectorAll('.nsx-pro-tab-content').forEach(function (x) {
                x.classList.remove('active');
            });

            button.classList.add('active');

            const target = document.getElementById('tab-' + tab);
            if (target) {
                target.classList.add('active');
            }
        });
    });
});


/* NSX Detail - büyük ürün görsel galerisi */
document.addEventListener('DOMContentLoaded', function () {
    const data = window.nsxDetailGalleryData;
    const modal = document.getElementById('nsxDetailGalleryModal');
    const trigger = document.querySelector('.nsx-detail-gallery-trigger');
    const image = document.getElementById('nsxDetailGalleryImage');
    const counter = document.getElementById('nsxDetailGalleryCounter');
    const thumbs = document.getElementById('nsxDetailGalleryThumbs');
    const prev = document.getElementById('nsxDetailGalleryPrev');
    const next = document.getElementById('nsxDetailGalleryNext');

    if (!data || !modal || !trigger || !image || !counter || !thumbs || !Array.isArray(data.images) || !data.images.length) {
        return;
    }

    let currentIndex = 0;

    function renderImage(index) {
        currentIndex = (index + data.images.length) % data.images.length;
        image.classList.remove('is-active');

        window.setTimeout(function () {
            image.src = data.images[currentIndex];
            image.alt = data.name + (nsxProductEnglish ? ' product image' : ' ürün görseli');
            counter.textContent = (currentIndex + 1) + ' / ' + data.images.length;

            thumbs.querySelectorAll('button').forEach(function (button, thumbIndex) {
                button.classList.toggle('active', thumbIndex === currentIndex);
            });

            image.classList.add('is-active');
        }, 80);
    }

    function buildThumbs() {
        thumbs.innerHTML = '';
        data.images.forEach(function (src, index) {
            const button = document.createElement('button');
            button.type = 'button';
            button.innerHTML = '<img src="' + src + '" alt="' + data.name + (nsxProductEnglish ? ' thumbnail ' : ' küçük görsel ') + (index + 1) + '">';
            button.addEventListener('click', function () {
                renderImage(index);
            });
            thumbs.appendChild(button);
        });
    }

    function openGallery() {
        buildThumbs();
        modal.classList.add('open');
        modal.setAttribute('aria-hidden', 'false');
        document.body.classList.add('nsx-gallery-lock');
        renderImage(currentIndex);
    }

    function closeGallery() {
        modal.classList.remove('open');
        modal.setAttribute('aria-hidden', 'true');
        document.body.classList.remove('nsx-gallery-lock');
    }

    trigger.addEventListener('click', openGallery);
    modal.querySelectorAll('[data-detail-gallery-close="true"]').forEach(function (el) {
        el.addEventListener('click', closeGallery);
    });

    if (prev) prev.addEventListener('click', function () { renderImage(currentIndex - 1); });
    if (next) next.addEventListener('click', function () { renderImage(currentIndex + 1); });

    document.addEventListener('keydown', function (event) {
        if (!modal.classList.contains('open')) return;
        if (event.key === 'Escape') closeGallery();
        if (event.key === 'ArrowLeft') renderImage(currentIndex - 1);
        if (event.key === 'ArrowRight') renderImage(currentIndex + 1);
    });
});

document.addEventListener('DOMContentLoaded', function () {
    const licenseInputs = Array.from(document.querySelectorAll('.detail-license-input'));
    const description = document.getElementById('detailBankTransferDescription');
    const amount = document.getElementById('detailBankAmount');
    const licenseName = document.getElementById('detailBankLicenseName');

    if (!licenseInputs.length || !description) {
        return;
    }

    function syncBankTransferInfo() {
        const selected = licenseInputs.find(function (input) {
            return input.checked;
        }) || licenseInputs[0];
        const isLifetime = selected && selected.value === 'Lifetime';

        description.textContent = isLifetime
            ? (description.dataset.lifetime || description.textContent)
            : (description.dataset.yearly || description.textContent);

        if (amount) {
            amount.textContent = isLifetime
                ? (amount.dataset.lifetime || amount.textContent)
                : (amount.dataset.yearly || amount.textContent);
        }

        if (licenseName) {
            licenseName.textContent = isLifetime ? (nsxProductEnglish ? 'Lifetime License' : 'Sınırsız Lisans') : (nsxProductEnglish ? 'Annual License' : 'Yıllık Lisans');
        }
    }

    licenseInputs.forEach(function (input) {
        input.addEventListener('change', syncBankTransferInfo);
    });

    syncBankTransferInfo();
});
