// Yerel bildirim kutusu — okunmamış sayısı dashboard rozetinde gösterilir.
import { useCallback, useEffect, useState } from "react";
import AsyncStorage from "@react-native-async-storage/async-storage";
import { isMockMode, nsxApi } from "@/src/api";
import { getSessionSync, subscribeSession, type Session } from "@/src/auth/session";
import type { InboxMessage } from "@/src/api/types";
import { forgetNotificationShown, markNotificationShown, wasNotificationShown, resetNotificationDedupe } from "./notification-dedupe";

export interface InboxItem {
  id: string;
  title: string;
  body: string;
  createdAt: string;
  read: boolean;
  category?: string;
}

function accountKey(session: Session | null): string | null {
  if (!session) return isMockMode ? "nsx.inbox.v2.mock" : null;
  return `nsx.inbox.v2.${session.mode}.${encodeURIComponent(session.tenantId)}.${encodeURIComponent(session.mobileDeviceId ?? session.pairedBy?.deviceId ?? "device")}`;
}
type InboxStore = { key: string | null; cache: InboxItem[] | null; queue: Promise<void>; sync: Promise<void> | null; lastSyncAt: number };
const createStore = (key: string | null): InboxStore => ({ key, cache: null, queue: Promise.resolve(), sync: null, lastSyncAt: 0 });
let currentStore = createStore(accountKey(getSessionSync()));
subscribeSession((session) => {
  const key = accountKey(session);
  if (key === currentStore.key) return;
  currentStore = createStore(key);
  resetNotificationDedupe();
  emit();
});
// The old unscoped store cannot safely be attributed to an account.
void AsyncStorage.removeItem("nsx.inbox.v1").catch(() => {});

const DEMO_SEED: InboxItem[] = [
  {
    id: "demo-1",
    title: "Yeni tahsilat",
    body: "Ahmet Yılmaz · 2.500,00 TL tahsilat kaydı eklendi.",
    createdAt: new Date(Date.now() - 12 * 60_000).toISOString(),
    read: false,
  },
  {
    id: "demo-2",
    title: "Vade hatırlatması",
    body: "3 müşterinin ödemesi bu hafta içinde.",
    createdAt: new Date(Date.now() - 3 * 3600_000).toISOString(),
    read: false,
  },
  {
    id: "demo-3",
    title: "Sistem",
    body: "NSX Cari Takip Pro mobil güncellemesi hazırlanıyor.",
    createdAt: new Date(Date.now() - 26 * 3600_000).toISOString(),
    read: false,
  },
];

const listeners = new Set<() => void>();
const MIN_SYNC_GAP_MS = 8_000;

function emit() { listeners.forEach((listener) => listener()); }

function serialize(store: InboxStore, action: () => Promise<void>): Promise<void> {
  const pending = store.queue.then(async () => { if (store === currentStore) await action(); });
  store.queue = pending.catch(() => {});
  return pending;
}

async function readStore(store = currentStore): Promise<InboxItem[]> {
  if (!store.key || store !== currentStore) return [];
  if (store.cache) return store.cache;
  const raw = await AsyncStorage.getItem(store.key);
  if (store !== currentStore) return [];
  try {
    const parsed: unknown = raw ? JSON.parse(raw) : null;
    store.cache = Array.isArray(parsed) ? parsed.filter((item): item is InboxItem =>
      !!item && typeof item.id === "string" && typeof item.title === "string" &&
      typeof item.body === "string" && typeof item.createdAt === "string") : null;
  } catch { store.cache = null; }
  store.cache ??= isMockMode ? DEMO_SEED.map((item) => ({ ...item })) : [];
  return store.cache;
}

async function writeStore(items: InboxItem[], store = currentStore): Promise<void> {
  if (!store.key || store !== currentStore) return;
  // Persist first; storage failures must not masquerade as a successful edit.
  await AsyncStorage.setItem(store.key, JSON.stringify(items));
  if (store !== currentStore) return;
  store.cache = items;
  emit();
}

function wireToItem(row: InboxMessage): InboxItem {
  return {
    id: row.id,
    title: row.title,
    body: row.body,
    createdAt: row.createdAtUtc,
    read: row.read,
    category: row.category,
  };
}

function mergeInbox(local: InboxItem[], remote: InboxMessage[]): InboxItem[] {
  const map = new Map<string, InboxItem>();
  for (const row of remote) map.set(row.id, wireToItem(row));
  for (const item of local) {
    if (!map.has(item.id)) map.set(item.id, item);
  }
  return [...map.values()]
    .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt))
    .slice(0, 50);
}

/** Live modda sunucu inbox ile yerel listeyi birleştir. */
export async function syncInboxFromServer(): Promise<void> {
  await refreshInboxFromServer({ notify: false });
}

/** Expo push geldiyse SignalR yerel banner'ı tekrar çalmasın. */
export function markInboxNotified(id?: string | null): void {
  markNotificationShown(id?.trim());
}

async function notifyInboxMessage(item: InboxMessage): Promise<void> {
  if (item.read || wasNotificationShown(item.id)) return;
  markNotificationShown(item.id);
  try {
    const { presentLocalNotification } = await import("./local-notify");
    const result = await presentLocalNotification(item.title, item.body, {
      category: item.category, messageId: item.id,
    });
    if (!result.ok) forgetNotificationShown(item.id);
  } catch {
    forgetNotificationShown(item.id);
  }
}

/** Committed server notification; no extra GET or timing dependency on sync. */
export async function receiveInboxMessage(item: InboxMessage): Promise<void> {
  const store = currentStore;
  await addInboxItem({ ...item, id: item.id, createdAt: item.createdAtUtc });
  if (store === currentStore && store.key) await notifyInboxMessage(item);
}

/**
 * Sunucudan çek. notify=true → yeni gelen okunmamışlar için OS banner.
 * Tek uçuş + min aralık: rate-limit (429) ve listener döngüsü yok.
 */
export async function refreshInboxFromServer(options?: { notify?: boolean }): Promise<void> {
  const store = currentStore;
  if (isMockMode || !store.key) return;
  if (store.sync) return store.sync;
  if (Date.now() - store.lastSyncAt < MIN_SYNC_GAP_MS && options?.notify !== true) return;
  const pending = serialize(store, async () => {
    store.lastSyncAt = Date.now();
    try {
      const remote = await nsxApi.getInbox();
      if (store !== currentStore) return;
      const local = await readStore(store);
      const localIds = new Set(local.map((item) => item.id));
      const brandNew = remote.filter((item) => !item.read && !localIds.has(item.id) && !wasNotificationShown(item.id));
      await writeStore(mergeInbox(local, remote), store);
      if (options?.notify === false || store !== currentStore) return;
      for (const item of brandNew) {
        if (store !== currentStore) return;
        await notifyInboxMessage(item);
      }
    } catch { /* Polling is best effort; explicit edits below propagate errors. */ }
  });
  store.sync = pending;
  try { await pending; } finally { if (store.sync === pending) store.sync = null; }
}

export function getUnreadCount(items: InboxItem[]): number {
  return items.filter((i) => !i.read).length;
}

export async function markAllRead(): Promise<void> {
  const store = currentStore;
  await serialize(store, async () => {
    const items = await readStore(store);
    const ids = items.filter((item) => !item.read).map((item) => item.id);
    if (!ids.length) return;
    if (!isMockMode) await nsxApi.markInboxRead({ ids });
    await writeStore(items.map((item) => ({ ...item, read: true })), store);
  });
}

export function addInboxItem(
  item: Omit<InboxItem, "id" | "createdAt" | "read"> & { id?: string; category?: string; createdAt?: string },
): Promise<void> {
  const store = currentStore;
  return serialize(store, async () => {
    const items = await readStore(store);
    if (item.id && items.some((existing) => existing.id === item.id)) return;
    const next: InboxItem = {
      id: item.id ?? `n-${Date.now()}-${Math.random().toString(36).slice(2)}`,
      title: item.title, body: item.body,
      createdAt: item.createdAt ?? new Date().toISOString(), read: false, category: item.category,
    };
    await writeStore([next, ...items].slice(0, 50), store);
  });
}

export async function getInboxItem(id: string): Promise<InboxItem | null> {
  const store = currentStore;
  const local = (await readStore(store)).find((item) => item.id === id);
  if (store !== currentStore) return null;
  if (local) return local;
  if (isMockMode || !store.key) return null;
  try {
    const remote = await nsxApi.getInboxMessage(id);
    return store === currentStore ? wireToItem(remote) : null;
  } catch { return null; }
}

export async function markInboxItemRead(id: string): Promise<void> {
  const store = currentStore;
  await serialize(store, async () => {
    const items = await readStore(store);
    const target = items.find((item) => item.id === id);
    if (!target || target.read) return;
    if (!isMockMode) await nsxApi.markInboxRead({ ids: [id] });
    await writeStore(items.map((item) => item.id === id ? { ...item, read: true } : item), store);
  });
}

export async function deleteInboxItem(id: string): Promise<void> {
  const store = currentStore;
  await serialize(store, async () => {
    const items = await readStore(store);
    if (!items.some((item) => item.id === id)) return;
    if (!isMockMode) await nsxApi.deleteInbox({ ids: [id] });
    await writeStore(items.filter((item) => item.id !== id), store);
  });
}

export async function clearInbox(): Promise<void> {
  const store = currentStore;
  await serialize(store, async () => {
    const items = await readStore(store);
    // Delete older server history too, while preserving notifications beyond this snapshot.
    const cutoff = items.reduce<string | undefined>((latest, item) =>
      !latest || Date.parse(item.createdAt) > Date.parse(latest) ? item.createdAt : latest, undefined);
    if (!isMockMode && cutoff) await nsxApi.deleteInbox({ all: true, createdBeforeUtc: cutoff });
    await writeStore([], store);
  });
}

export function categoryLabel(category?: string): string {
  switch ((category ?? "").toLowerCase()) {
    case "collections":
      return "Tahsilat";
    case "debts":
      return "Borç";
    case "reminders":
      return "Hatırlatma";
    case "system":
      return "Sistem";
    default:
      return "Bildirim";
  }
}

/** Dashboard / ayarlar — canlı okunmamış sayısı. */
export function useNotificationInbox() {
  const [items, setItems] = useState<InboxItem[]>([]);
  const [ready, setReady] = useState(false);

  const applyLocal = useCallback(async () => {
    const store = currentStore;
    const list = await readStore(store);
    if (store !== currentStore) return;
    setItems(list);
    setReady(true);
  }, []);

  const refresh = useCallback(async () => {
    if (!isMockMode) await refreshInboxFromServer({ notify: false });
    await applyLocal();
  }, [applyLocal]);

  useEffect(() => {
    void Promise.resolve().then(refresh).catch(() => setReady(true));
    // Listener sadece yereli yansıtır — tekrar GET /inbox ÇAĞIRMAZ (429 döngüsü).
    const onChange = () => {
      setItems([]);
      void applyLocal().catch(() => setReady(true));
    };
    listeners.add(onChange);
    return () => {
      listeners.delete(onChange);
    };
  }, [refresh, applyLocal]);

  const unreadCount = getUnreadCount(items);

  const markRead = useCallback(async () => {
    await markAllRead();
  }, []);

  return { items, unreadCount, ready, markRead, refresh };
}

export function formatInboxTime(iso: string): string {
  const ms = Date.parse(iso);
  if (!Number.isFinite(ms)) return "";
  const diff = Date.now() - ms;
  if (diff < 60_000) return "Az önce";
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)} dk`;
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)} sa`;
  return `${Math.floor(diff / 86_400_000)} g`;
}
