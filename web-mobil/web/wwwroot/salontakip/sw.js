const CACHE_NAME = 'nsx-salon-shell-v1';
const OFFLINE_URL = '/salontakip/offline.html';
const SHELL_ASSETS = [
  OFFLINE_URL,
  '/salontakip/app-icon.svg',
  '/salontakip/app-icon-192.png',
  '/salontakip/app-icon-512.png'
];

self.addEventListener('install', event => {
  event.waitUntil(caches.open(CACHE_NAME).then(cache => cache.addAll(SHELL_ASSETS)));
  self.skipWaiting();
});

self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(key => key !== CACHE_NAME).map(key => caches.delete(key))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', event => {
  if (event.request.method !== 'GET' || event.request.mode !== 'navigate') return;
  event.respondWith(fetch(event.request).catch(() => caches.match(OFFLINE_URL)));
});
