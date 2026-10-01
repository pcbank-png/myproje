const slides = document.querySelectorAll('.slide');
const dots = document.querySelectorAll('.dot');
const nextBtn = document.querySelector('.next');
const prevBtn = document.querySelector('.prev');

let currentSlide = 0;
let timer = null;

function showSlide(index){
    if(!slides.length || !dots.length) return;
    if(index >= slides.length){ index = 0; }
    if(index < 0){ index = slides.length - 1; }

    slides.forEach(slide => slide.classList.remove('active'));
    dots.forEach(dot => dot.classList.remove('active'));

    slides[index].classList.add('active');
    dots[index].classList.add('active');
    currentSlide = index;
}

function nextSlide(){ showSlide(currentSlide + 1); }
function prevSlide(){ showSlide(currentSlide - 1); }

function startSlider(){
    if(!slides.length) return;
    stopSlider();
    timer = setInterval(nextSlide, 4500);
}

function stopSlider(){
    if(timer){ clearInterval(timer); }
}

if(nextBtn && prevBtn && slides.length && dots.length){
    nextBtn.addEventListener('click', () => { nextSlide(); startSlider(); });
    prevBtn.addEventListener('click', () => { prevSlide(); startSlider(); });

    dots.forEach((dot, index) => {
        dot.addEventListener('click', () => {
            showSlide(index);
            startSlider();
        });
    });

    startSlider();
}

/* Mobile menu is handled once inside DOMContentLoaded below. */

const zoomBox = document.getElementById('zoomBox');
const zoomLens = document.getElementById('zoomLens');
const mainProductImg = document.getElementById('mainProductImg');
const thumbs = document.querySelectorAll('.thumb');

thumbs.forEach(thumb => {
    thumb.addEventListener('click', () => {
        thumbs.forEach(t => t.classList.remove('active'));
        thumb.classList.add('active');
        const img = thumb.getAttribute('data-img');
        if(mainProductImg && img){
            mainProductImg.src = img;
        }
    });
});

if(zoomBox && zoomLens && mainProductImg){
    zoomBox.addEventListener('mouseenter', () => {
        if(window.innerWidth < 901) return;
        zoomBox.classList.add('zoom-active');
    });

    zoomBox.addEventListener('mouseleave', () => {
        zoomBox.classList.remove('zoom-active');
        mainProductImg.style.transformOrigin = 'center center';
        zoomLens.style.left = '50%';
        zoomLens.style.top = '50%';
    });

    zoomBox.addEventListener('mousemove', (e) => {
        if(window.innerWidth < 901) return;
        const rect = zoomBox.getBoundingClientRect();
        const x = Math.max(0, Math.min(e.clientX - rect.left, rect.width));
        const y = Math.max(0, Math.min(e.clientY - rect.top, rect.height));
        const xPercent = (x / rect.width) * 100;
        const yPercent = (y / rect.height) * 100;

        mainProductImg.style.transformOrigin = `${xPercent}% ${yPercent}%`;
        zoomLens.style.left = `${x - 63}px`;
        zoomLens.style.top = `${y - 63}px`;
    });
}

const tabButtons = document.querySelectorAll('.tab-btn');
const tabContents = document.querySelectorAll('.tab-content');

tabButtons.forEach(btn => {
    btn.addEventListener('click', () => {
        tabButtons.forEach(b => b.classList.remove('active'));
        tabContents.forEach(c => c.classList.remove('active'));
        btn.classList.add('active');
        const target = document.getElementById(btn.dataset.tab);
        if(target) target.classList.add('active');
    });
});


const discountBox = document.querySelector('.nsx-discount-box');
const discountCodeInput = document.getElementById('discountCode');
const applyDiscountBtn = document.getElementById('applyDiscountBtn');
const discountMessage = document.getElementById('discountMessage');

function formatTRY(value){
    return new Intl.NumberFormat('tr-TR', { style:'currency', currency:'TRY' }).format(value);
}

if(discountBox && discountCodeInput && applyDiscountBtn && discountMessage){
    const adminCode = (discountBox.dataset.adminDiscountCode || '').toUpperCase();
    const adminType = discountBox.dataset.adminDiscountType || 'amount';
    const adminValue = Number(discountBox.dataset.adminDiscountValue || 0);

    applyDiscountBtn.addEventListener('click', () => {
        const enteredCode = discountCodeInput.value.trim().toUpperCase();
        discountMessage.classList.remove('success','error');

        if(!enteredCode){
            discountMessage.textContent = 'Lütfen indirim kodunuzu yazın.';
            discountMessage.classList.add('error');
            return;
        }

        if(enteredCode !== adminCode){
            discountMessage.textContent = 'Bu indirim kodu geçerli değil veya süresi dolmuş.';
            discountMessage.classList.add('error');
            return;
        }

        const discountData = {
            code: adminCode,
            type: adminType,
            value: adminValue,
            source: 'admin',
            productId: 'nsx-veresiye'
        };

        localStorage.setItem('nsxPurchaseDiscount', JSON.stringify(discountData));
        const label = adminType === 'percent' ? `%${adminValue}` : formatTRY(adminValue);
        discountMessage.textContent = `${adminCode} kodu aktif edildi. ${label} indirim satın alma bildirimi sırasında değerlendirilecek.`;
        discountMessage.classList.add('success');
    });
}

const ratingPicker = document.getElementById('ratingPicker');
const ratingButtons = ratingPicker ? ratingPicker.querySelectorAll('button') : [];
const submitReviewBtn = document.getElementById('submitReviewBtn');
const reviewName = document.getElementById('reviewName');
const reviewTitle = document.getElementById('reviewTitle');
const reviewText = document.getElementById('reviewText');
const reviewMessage = document.getElementById('reviewMessage');
const reviewList = document.getElementById('reviewList');
const reviewAverage = document.getElementById('reviewAverage');
const reviewAverageStars = document.getElementById('reviewAverageStars');
const reviewCount = document.getElementById('reviewCount');
let selectedRating = 5;

function setRatingStars(rating){
    selectedRating = Number(rating);
    ratingButtons.forEach(btn => {
        const value = Number(btn.dataset.rating);
        btn.classList.toggle('selected', value <= selectedRating);
        btn.classList.toggle('active', value <= selectedRating);
    });
}

function renderStars(rating){
    const full = '★★★★★'.slice(0, rating);
    const empty = '☆☆☆☆☆'.slice(0, 5 - rating);
    return full + empty;
}

function updateReviewSummary(){
    if(!reviewList || !reviewAverage || !reviewAverageStars || !reviewCount) return;
    const items = Array.from(reviewList.querySelectorAll('.review-item'));
    const total = items.reduce((sum, item) => sum + Number(item.dataset.rating || 5), 0);
    const avg = items.length ? total / items.length : 0;
    reviewAverage.textContent = avg.toFixed(1);
    reviewAverageStars.textContent = renderStars(Math.round(avg));
    reviewCount.textContent = items.length;
}

function addReviewItem(name, title, text, rating){
    if(!reviewList) return;
    const item = document.createElement('div');
    item.className = 'review-item';
    item.dataset.rating = rating;
    item.innerHTML = `<div><strong>${title}</strong><span>${renderStars(rating)}</span></div><p>${text}</p><em>${name}</em>`;
    reviewList.prepend(item);
}

if(ratingPicker && ratingButtons.length){
    setRatingStars(5);
    ratingButtons.forEach(btn => {
        btn.addEventListener('click', () => setRatingStars(btn.dataset.rating));
    });
}

if(submitReviewBtn && reviewName && reviewTitle && reviewText && reviewMessage){
    submitReviewBtn.addEventListener('click', () => {
        const name = reviewName.value.trim();
        const title = reviewTitle.value.trim();
        const text = reviewText.value.trim();
        reviewMessage.classList.remove('success','error');

        if(!name || !title || !text){
            reviewMessage.textContent = 'Lütfen ad, başlık ve yorum alanlarını doldurun.';
            reviewMessage.classList.add('error');
            return;
        }

        const reviewData = {
            productId: 'nsx-veresiye',
            name,
            title,
            text,
            rating: selectedRating,
            status: 'pending-admin-approval',
            createdAt: new Date().toISOString()
        };

        const savedReviews = JSON.parse(localStorage.getItem('nsxProductReviews') || '[]');
        savedReviews.push(reviewData);
        localStorage.setItem('nsxProductReviews', JSON.stringify(savedReviews));

        addReviewItem(name, title, text, selectedRating);
        updateReviewSummary();
        reviewMessage.textContent = 'Yorumunuz alındı. Admin onayından sonra yayına alınacak.';
        reviewMessage.classList.add('success');
        reviewName.value = '';
        reviewTitle.value = '';
        reviewText.value = '';
        setRatingStars(5);
    });
}

updateReviewSummary();

/* MOBILE MENU OPEN FIX */
document.addEventListener('DOMContentLoaded', function () {
    var toggle = document.getElementById('mobileMenuToggle') || document.querySelector('.mobile-menu-toggle');
    var menu = document.getElementById('mainMenu') || document.querySelector('.main-menu');

    if (toggle && menu) {
        toggle.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            menu.classList.toggle('active');
            toggle.classList.toggle('active');
            document.body.classList.toggle('menu-open');
        });

        menu.querySelectorAll('a').forEach(function (link) {
            link.addEventListener('click', function () {
                menu.classList.remove('active');
                toggle.classList.remove('active');
                document.body.classList.remove('menu-open');
            });
        });
    }
});

/* LOGIN DROPDOWN JS */
document.addEventListener('DOMContentLoaded', function(){
    document.querySelectorAll('.login-dropdown .login-toggle').forEach(function(btn){
        btn.addEventListener('click', function(e){
            e.preventDefault();
            e.stopPropagation();

            var wrap = btn.closest('.login-dropdown');
            document.querySelectorAll('.login-dropdown.active').forEach(function(item){
                if(item !== wrap) item.classList.remove('active');
            });

            if(wrap){
                wrap.classList.toggle('active');
                btn.setAttribute('aria-expanded', wrap.classList.contains('active') ? 'true' : 'false');
            }
        });
    });

    document.addEventListener('click', function(){
        document.querySelectorAll('.login-dropdown.active').forEach(function(item){
            item.classList.remove('active');
            var btn = item.querySelector('.login-toggle');
            if(btn) btn.setAttribute('aria-expanded','false');
        });
    });
});

/* NSX LIVE SUPPORT + TOP */
document.addEventListener('DOMContentLoaded', function(){

const nsxLiveToggle = document.getElementById('nsxLiveToggle');
const nsxLiveBox = document.getElementById('nsxLiveBox');
const nsxLiveClose = document.getElementById('nsxLiveClose');
const nsxScrollTop = document.getElementById('nsxScrollTop');

if(nsxLiveToggle && nsxLiveBox){
    nsxLiveToggle.addEventListener('click',()=>{
        nsxLiveBox.classList.toggle('active');
    });
}

if(nsxLiveClose && nsxLiveBox){
    nsxLiveClose.addEventListener('click',()=>{
        nsxLiveBox.classList.remove('active');
    });
}

if(nsxScrollTop){
    window.addEventListener('scroll',()=>{
        if(window.scrollY > 260){
            nsxScrollTop.classList.add('show');
        }else{
            nsxScrollTop.classList.remove('show');
        }
    });

    nsxScrollTop.addEventListener('click',()=>{
        window.scrollTo({
            top:0,
            behavior:'smooth'
        });
    });
}

});
