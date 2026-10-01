window.nsxIsEnglish = function () {
    return (document.documentElement.lang || '').toLowerCase().startsWith('en');
};
window.nsxUiText = function (tr, en) {
    return window.nsxIsEnglish() ? en : tr;
};

window.nsxShowAlert = function (message, type, title) {
    const modalElement = document.getElementById('nsxAlertModal');

    if (!modalElement || !window.bootstrap) {
        alert(message || window.nsxUiText('Bilgilendirme', 'Information'));
        return;
    }

    const icon = document.getElementById('nsxAlertIcon');
    const titleEl = document.getElementById('nsxAlertTitle');
    const messageEl = document.getElementById('nsxAlertMessage');

    const alertType = type || 'info';

    const map = {
        success: { icon: '✓', title: title || window.nsxUiText('İşlem Başarılı', 'Success') },
        error: { icon: '!', title: title || window.nsxUiText('İşlem Başarısız', 'Operation Failed') },
        warning: { icon: '!', title: title || window.nsxUiText('Dikkat', 'Warning') },
        info: { icon: 'i', title: title || window.nsxUiText('Bilgilendirme', 'Information') }
    };

    const selected = map[alertType] || map.info;

    icon.textContent = selected.icon;
    titleEl.textContent = selected.title;
    messageEl.textContent = message || window.nsxUiText('İşlem tamamlandı.', 'Operation completed.');

    const modal = new bootstrap.Modal(modalElement);
    modal.show();
};

window.nsxCopyText = function (elementId) {
    const element = document.getElementById(elementId);
    if (!element) return;

    const text = element.innerText || element.textContent || "";

    navigator.clipboard.writeText(text).then(function () {
        window.nsxShowAlert(window.nsxUiText('Lisans anahtarı kopyalandı.', 'License key copied.'), 'success', window.nsxUiText('Kopyalandı', 'Copied'));
    }).catch(function () {
        const input = document.createElement("textarea");
        input.value = text;
        document.body.appendChild(input);
        input.select();
        document.execCommand("copy");
        document.body.removeChild(input);
        window.nsxShowAlert(window.nsxUiText('Lisans anahtarı kopyalandı.', 'License key copied.'), 'success', window.nsxUiText('Kopyalandı', 'Copied'));
    });
};

document.addEventListener('DOMContentLoaded', function () {
    if (window.nsxStartupAlert && window.nsxStartupAlert.message) {
        window.nsxShowAlert(window.nsxStartupAlert.message, window.nsxStartupAlert.type);
    }

    document.querySelectorAll('[data-nsx-alert]').forEach(function (el) {
        el.addEventListener('click', function () {
            window.nsxShowAlert(
                el.getAttribute('data-nsx-alert'),
                el.getAttribute('data-nsx-alert-type') || 'info',
                el.getAttribute('data-nsx-alert-title') || ''
            );
        });
    });
});


// NSX premium product card spotlight + magnetic button effects
(function () {
    function clamp(value, min, max) {
        return Math.min(Math.max(value, min), max);
    }

    document.querySelectorAll('.nsx-store-pro-card').forEach(function (card) {
        card.addEventListener('mousemove', function (event) {
            var rect = card.getBoundingClientRect();
            var x = ((event.clientX - rect.left) / rect.width) * 100;
            var y = ((event.clientY - rect.top) / rect.height) * 100;
            card.style.setProperty('--nsx-spot-x', clamp(x, 0, 100) + '%');
            card.style.setProperty('--nsx-spot-y', clamp(y, 0, 100) + '%');
        });

        card.addEventListener('mouseleave', function () {
            card.style.setProperty('--nsx-spot-x', '50%');
            card.style.setProperty('--nsx-spot-y', '50%');
        });
    });

    document.querySelectorAll('.nsx-btn-magnetic').forEach(function (button) {
        button.addEventListener('mousemove', function (event) {
            var rect = button.getBoundingClientRect();
            var x = event.clientX - rect.left - rect.width / 2;
            var y = event.clientY - rect.top - rect.height / 2;
            button.style.transform = 'translate(' + (x * 0.10) + 'px, ' + (y * 0.10) + 'px) scale(1.02)';
        });

        button.addEventListener('mouseleave', function () {
            button.style.transform = '';
        });
    });
})();


// NSX SignalR canlı destek popup + gerçek zamanlı mesajlaşma
(function () {
    document.addEventListener('DOMContentLoaded', function () {
        var widget = document.getElementById('nsxLiveSupportWidget');
        var openButton = document.getElementById('nsxLiveSupportButton');
        var closeButton = document.getElementById('nsxLiveSupportClose');
        var windowEl = document.getElementById('nsxLiveSupportWindow');
        var form = document.getElementById('nsxLiveSupportForm');
        var input = document.getElementById('nsxLiveSupportMessage');
        var contactInput = document.getElementById('nsxLiveSupportContact');
        var messagesEl = document.getElementById('nsxLiveChatMessages');
        var statusText = document.getElementById('nsxLiveSupportStatus');

        if (!widget || !openButton || !closeButton || !windowEl) return;

        function setOpen(isOpen) {
            widget.classList.toggle('is-open', isOpen);
            windowEl.setAttribute('aria-hidden', isOpen ? 'false' : 'true');
            if (isOpen && input) setTimeout(function () { input.focus(); }, 220);
        }

        function setStatus(text, cls) {
            if (!statusText) return;
            statusText.textContent = text;
            statusText.className = 'nsx-live-support-status ' + (cls || '');
        }

        function escapeHtml(text) {
            return (text || '').replace(/[&<>'"]/g, function (c) {
                return ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[c];
            });
        }

        function addMessage(message, side) {
            if (!messagesEl || !message || !message.text) return;
            var cls = side || (message.sender === 'Admin' ? 'admin' : 'customer');
            var senderLabel = window.nsxIsEnglish() && message.sender === 'Müşteri' ? 'Customer' : (message.sender || '');
            var html = '<div class="nsx-realtime-message ' + cls + '">' +
                '<div>' + escapeHtml(message.text) + '</div>' +
                '<small>' + escapeHtml(senderLabel) + ' · ' + escapeHtml(message.createdAt || '') + '</small>' +
                '</div>';
            messagesEl.insertAdjacentHTML('beforeend', html);
            messagesEl.scrollTop = messagesEl.scrollHeight;
        }

        function beep() {
            try {
                var ctx = new (window.AudioContext || window.webkitAudioContext)();
                var osc = ctx.createOscillator();
                var gain = ctx.createGain();
                osc.type = 'triangle';
                osc.frequency.value = 740;
                gain.gain.value = 0.055;
                osc.connect(gain);
                gain.connect(ctx.destination);
                osc.start();
                setTimeout(function () { osc.stop(); ctx.close(); }, 140);
            } catch (e) { }
        }

        openButton.addEventListener('click', function () {
            setOpen(!widget.classList.contains('is-open'));
        });

        closeButton.addEventListener('click', function () { setOpen(false); });

        document.addEventListener('keydown', function (event) {
            if (event.key === 'Escape') setOpen(false);
        });

        var sessionId = localStorage.getItem('nsx_live_chat_session');
        if (!sessionId) {
            sessionId = 'guest-' + Date.now() + '-' + Math.random().toString(16).slice(2);
            localStorage.setItem('nsx_live_chat_session', sessionId);
        }

        if (typeof signalR === 'undefined') {
            setStatus(window.nsxUiText('Canlı destek bağlantısı kurulamadı.', 'Live support connection could not be established.'), 'is-error');
            return;
        }

        var connection = new signalR.HubConnectionBuilder()
            .withUrl('/liveChatHub')
            .withAutomaticReconnect()
            .build();

        connection.on('SessionReady', function (data) {
            setStatus(window.nsxUiText('Canlı destek hazır. Mesajınız anlık iletilir.', 'Live support is ready. Your message will be delivered instantly.'), 'is-success');
            if (data && data.messages && data.messages.length && messagesEl) {
                data.messages.forEach(function (m) {
                    if (m.sender === 'Admin' || m.sender === 'Müşteri') {
                        addMessage(m, m.sender === 'Admin' ? 'admin' : 'customer');
                    }
                });
            }
        });

        connection.on('ReceiveCustomerMessage', function (message) {
            addMessage(message, 'customer');
        });

        connection.on('ReceiveAdminReply', function (message) {
            addMessage(message, 'admin');
            setOpen(true);
            setStatus(window.nsxUiText('Admin cevap verdi.', 'Admin replied.'), 'is-success');
            beep();
        });

        connection.start().then(function () {
            setStatus(window.nsxUiText('Canlı destek hazır. Mesajınız anlık iletilir.', 'Live support is ready. Your message will be delivered instantly.'), 'is-success');
            return connection.invoke('CustomerJoin', sessionId, contactInput ? contactInput.value : '');
        }).catch(function () {
            setStatus(window.nsxUiText('Canlı destek bağlantısı kurulamadı. Lütfen tekrar deneyin.', 'Live support connection could not be established. Please try again.'), 'is-error');
        });

        if (form) {
            form.addEventListener('submit', function (event) {
                event.preventDefault();
                var text = input ? input.value.trim() : '';
                var contact = contactInput ? contactInput.value.trim() : '';
                if (!text) {
                    setStatus(window.nsxUiText('Lütfen mesajınızı yazın.', 'Please enter your message.'), 'is-error');
                    return;
                }

                if (connection.state !== signalR.HubConnectionState.Connected) {
                    setStatus(window.nsxUiText('Bağlantı hazırlanıyor, birkaç saniye sonra tekrar deneyin.', 'Connection is being prepared. Please try again in a few seconds.'), 'is-loading');
                    return;
                }

                connection.invoke('CustomerSend', sessionId, contact, text).then(function () {
                    input.value = '';
                    setStatus(window.nsxUiText('Mesajınız canlı destek ekranına iletildi.', 'Your message was delivered to live support.'), 'is-success');
                }).catch(function () {
                    setStatus(window.nsxUiText('Mesaj gönderilemedi. Lütfen tekrar deneyin.', 'Message could not be sent. Please try again.'), 'is-error');
                });
            });
        }

        document.querySelectorAll('.nsx-wp-app-link').forEach(function (button) {
            button.addEventListener('click', function (event) {
                event.preventDefault();
                var phone = button.getAttribute('data-phone') || '905434624226';
                var message = input && input.value.trim() ? input.value.trim() : window.nsxUiText('Merhaba NSX Yazılım, destek almak istiyorum.', 'Hello NSX Software, I would like support.');
                var url = 'https://wa.me/' + phone + '?text=' + encodeURIComponent(message);
                window.open(url, 'nsxWhatsAppSupport', 'width=460,height=720,menubar=no,toolbar=no,location=yes,status=no,scrollbars=yes,resizable=yes');
            });
        });
    });
})();
