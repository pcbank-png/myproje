"use strict";
const cacheName="nsx-cari-shell-v20";
const shell=["/caritakip/app.css?v=20260914-final20","/caritakip/app.js?v=20260914-final20","/caritakip/app-icon.svg","/caritakip/manifest.webmanifest","/caritakip/offline.html"];
self.addEventListener("install",event=>event.waitUntil(caches.open(cacheName).then(cache=>cache.addAll(shell)).then(()=>self.skipWaiting())));
self.addEventListener("activate",event=>event.waitUntil(caches.keys().then(keys=>Promise.all(keys.filter(x=>x.startsWith("nsx-cari-shell-")&&x!==cacheName).map(x=>caches.delete(x)))).then(()=>self.clients.claim())));
self.addEventListener("fetch",event=>{
  if(event.request.method!=="GET"||new URL(event.request.url).pathname.toLowerCase().startsWith("/api/"))return;
  event.respondWith(fetch(event.request).catch(()=>caches.match(event.request).then(response=>response||caches.match("/caritakip/offline.html"))));
});
