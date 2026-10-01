// Mock NSX API client — mirrors the live HTTP client contract so the
// screens work identically in both modes. All state is in-memory; only
// the *session* is persisted (in SecureStore, keyed to mock mode).
// Mock mode NEVER contacts the network and is completely isolated from
// live mode.

import { seedCompanies, seedCustomers, seedTransactions } from "./mock-seed";
import {
  NsxAuthError,
  type Company,
  type ConnectedDevice,
  type Customer,
  type CustomerId,
  type CustomerInput,
  type DashboardStatus,
  type DeviceInfo,
  type InviteResult,
  type PairingResult,
  type PushTokenInput,
  type PushTokenResult,
  type InboxMessage,
  type InboxMarkReadInput,
  type InboxDeleteInput,
  type NotificationPrefs,
  type RefreshResult,
  type Transaction,
  type TransactionId,
  type TransactionInput,
  type TransactionKind,
} from "./types";

const delay = (ms = 250) => new Promise((r) => setTimeout(r, ms));
const uid = (prefix: string) =>
  `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;

// Deterministic mock lifetimes — long enough for `restart → still logged in`
// UX to feel real, short access token so silent refresh actually runs.
const ACCESS_TOKEN_TTL_MS = 5 * 60_000; // 5 dk
const REFRESH_TOKEN_TTL_MS = 30 * 24 * 3600_000; // 30 gün

let companies: Company[] = seedCompanies.map((c) => ({ ...c }));
let customers: Customer[] = seedCustomers.map((c) => ({ ...c, version: 1 }));
let transactions: Transaction[] = seedTransactions.map((t) => ({ ...t }));

// Track "revoked" refresh tokens in mock so logout ↔ subsequent refresh
// behaves like the real backend (401 → force re-scan).
const revokedRefreshTokens = new Set<string>();
const activeRefreshTokens = new Set<string>();

function recomputeBalances() {
  const balances = new Map<CustomerId, number>();
  for (const t of transactions) {
    const cur = balances.get(t.customerId) ?? 0;
    balances.set(t.customerId, cur + (t.kind === "debt" ? t.amount : -t.amount));
  }
  customers = customers.map((c) => ({ ...c, netBalance: balances.get(c.id) ?? 0 }));
}
recomputeBalances();

function issueTokenPair() {
  const now = Date.now();
  const accessToken = `mock.access.${uid("a")}`;
  const refreshToken = `mock.refresh.${uid("r")}`;
  activeRefreshTokens.add(refreshToken);
  return {
    accessToken,
    refreshToken,
    accessTokenExpiresAt: new Date(now + ACCESS_TOKEN_TTL_MS).toISOString(),
    refreshTokenExpiresAt: new Date(now + REFRESH_TOKEN_TTL_MS).toISOString(),
  };
}

export const mockApi = {
  async pair(code: string, _deviceInfo?: DeviceInfo): Promise<PairingResult> {
    await delay(500);
    if (!code || code.trim().length < 4) {
      throw new Error("Eşleştirme kodu geçersiz. En az 4 karakter olmalı.");
    }
    const tokens = issueTokenPair();
    return {
      ...tokens,
      tenantId: "mock-tenant",
      companyName: companies[0]?.name ?? "",
      pairedBy: { deviceId: "demo-desktop", displayName: "Demo Kullanıcı" },
      companies: [...companies],
      defaultCompanyId: companies[0]?.id ?? "",
    };
  },

  async refresh(refreshToken: string): Promise<RefreshResult> {
    await delay(150);
    if (!refreshToken || !refreshToken.startsWith("mock.refresh.")) {
      throw new NsxAuthError("TOKEN_INVALID", "Refresh token geçersiz.");
    }
    if (revokedRefreshTokens.has(refreshToken)) {
      throw new NsxAuthError("REFRESH_REVOKED", "Oturum sonlandırıldı.");
    }
    if (!activeRefreshTokens.has(refreshToken)) {
      // Rotation replay protection: only the most recently issued token
      // is valid; older tokens are considered revoked.
      throw new NsxAuthError("REFRESH_REVOKED", "Refresh token değiştirildi.");
    }
    // Rotate.
    activeRefreshTokens.delete(refreshToken);
    return issueTokenPair();
  },

  async logout(refreshToken: string): Promise<void> {
    await delay(100);
    if (refreshToken) {
      revokedRefreshTokens.add(refreshToken);
      activeRefreshTokens.delete(refreshToken);
    }
  },

  async getCompanies(): Promise<Company[]> {
    await delay(100);
    return [...companies];
  },

  async getStatus(companyId: string): Promise<DashboardStatus> {
    await delay(200);
    const company = companies.find((c) => c.id === companyId);
    const scoped = customers.filter((c) => c.companyId === companyId);
    const totalReceivable = transactions
      .filter((t) => t.companyId === companyId && t.kind === "debt")
      .reduce((s, t) => s + t.amount, 0);
    const totalCollected = transactions
      .filter((t) => t.companyId === companyId && t.kind === "collection")
      .reduce((s, t) => s + t.amount, 0);
    const netBalance = totalReceivable - totalCollected;
    return {
      companyId,
      companyName: company?.name ?? "",
      totalReceivable,
      totalCollected,
      netBalance,
      customerCount: scoped.length,
      updatedAt: new Date().toISOString(),
    };
  },

  async getCustomers(companyId: string, search?: string): Promise<Customer[]> {
    await delay(150);
    const q = (search ?? "").trim().toLocaleLowerCase("tr");
    return customers
      .filter((c) => c.companyId === companyId)
      .filter((c) => !q || c.name.toLocaleLowerCase("tr").includes(q) || (c.phone ?? "").includes(q))
      .sort((a, b) => b.netBalance - a.netBalance);
  },

  async getCustomer(id: CustomerId): Promise<Customer | undefined> {
    await delay(100);
    const found = customers.find((c) => c.id === id);
    // Live davranışıyla tutarlı: kayıt yoksa hata fırlat (404).
    // TanStack Query `undefined` query data kabul etmez.
    if (!found) throw new Error("Müşteri bulunamadı.");
    return found;
  },

  async createCustomer(companyId: string, input: CustomerInput): Promise<Customer> {
    await delay(300);
    const name = input.name.trim();
    if (!name) throw new Error("Müşteri adı zorunlu.");
    const now = new Date().toISOString();
    const created: Customer = {
      id: uid("m"),
      companyId,
      name,
      phone: input.phone?.trim() || undefined,
      email: input.email?.trim() || undefined,
      note: input.note?.trim() || undefined,
      netBalance: 0,
      version: 1,
      updatedAt: now,
    };
    customers = [created, ...customers];
    return created;
  },

  async updateCustomer(id: CustomerId, input: CustomerInput, _expectedVersion?: number): Promise<Customer> {
    await delay(250);
    const idx = customers.findIndex((c) => c.id === id);
    if (idx < 0) throw new Error("Müşteri bulunamadı.");
    const updated: Customer = {
      ...customers[idx],
      version: customers[idx].version + 1,
      name: input.name.trim() || customers[idx].name,
      phone: input.phone?.trim() || undefined,
      email: input.email?.trim() || undefined,
      note: input.note?.trim() || undefined,
      updatedAt: new Date().toISOString(),
    };
    customers[idx] = updated;
    transactions = transactions.map((t) =>
      t.customerId === id ? { ...t, customerName: updated.name } : t,
    );
    return updated;
  },

  async deleteCustomer(id: CustomerId, _expectedVersion?: number): Promise<void> {
    await delay(200);
    customers = customers.filter((c) => c.id !== id);
    transactions = transactions.filter((t) => t.customerId !== id);
    recomputeBalances();
  },

  async getRecentTransactions(companyId: string, take = 20): Promise<Transaction[]> {
    await delay(150);
    return transactions
      .filter((t) => t.companyId === companyId)
      .sort((a, b) => (a.date < b.date ? 1 : -1))
      .slice(0, take);
  },

  async getCustomerTransactions(customerId: CustomerId): Promise<Transaction[]> {
    await delay(150);
    return transactions
      .filter((t) => t.customerId === customerId)
      .sort((a, b) => (a.date < b.date ? 1 : -1));
  },

  async createTransaction(companyId: string, input: TransactionInput): Promise<Transaction> {
    await delay(300);
    const customer = customers.find((c) => c.id === input.customerId);
    if (!customer) throw new Error("Müşteri bulunamadı.");
    if (!(input.amount > 0)) throw new Error("Tutar 0'dan büyük olmalı.");
    const now = new Date().toISOString();
    const created: Transaction = {
      id: uid("t"),
      companyId,
      customerId: input.customerId,
      customerName: customer.name,
      kind: input.kind,
      amount: Math.round(input.amount * 100) / 100,
      description: input.description?.trim() || undefined,
      date: input.date ?? now,
      createdAt: now,
      version: 1,
    };
    transactions = [created, ...transactions];
    recomputeBalances();
    return created;
  },

  async updateTransaction(id: TransactionId, input: TransactionInput, _expectedVersion?: number): Promise<Transaction> {
    await delay(250);
    const idx = transactions.findIndex((t) => t.id === id);
    if (idx < 0) throw new Error("Hareket bulunamadı.");
    const cur = transactions[idx];
    const updated: Transaction = {
      ...cur,
      kind: input.kind,
      amount: Math.round(input.amount * 100) / 100,
      description: input.description?.trim() || undefined,
      date: input.date ?? cur.date,
      version: cur.version + 1,
    };
    transactions[idx] = updated;
    recomputeBalances();
    return updated;
  },

  async deleteTransaction(id: TransactionId, _kind?: TransactionKind, _expectedVersion?: number): Promise<void> {
    await delay(200);
    transactions = transactions.filter((t) => t.id !== id);
    recomputeBalances();
  },

  async createInvite(_companyId: string): Promise<InviteResult> {
    await delay(400);
    const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    let raw = "";
    for (let i = 0; i < 8; i++) raw += alphabet[Math.floor(Math.random() * alphabet.length)];
    const code = `NSX-${raw.slice(0, 4)}-${raw.slice(4)}`;
    return {
      code,
      qrPayload: `nsx://pair?code=${code}`,
      expiresAt: new Date(Date.now() + 3 * 60_000).toISOString(),
    };
  },

  async getDevices(): Promise<ConnectedDevice[]> {
    await delay(200);
    const now = Date.now();
    return [
      {
        id: "self",
        name: "Bu cihaz",
        platform: "ios",
        appVersion: "1.0.0",
        lastSeenAt: new Date(now).toISOString(),
        issuedAt: new Date(now - 12 * 3600_000).toISOString(),
        status: "active",
        self: true,
      },
      {
        id: "d1",
        name: "Muhasebe iPad",
        platform: "ios",
        appVersion: "1.0.0",
        lastSeenAt: new Date(now - 5 * 60_000).toISOString(),
        issuedAt: new Date(now - 30 * 86400_000).toISOString(),
        status: "active",
      },
      {
        id: "d2",
        name: "Samsung Galaxy S24",
        platform: "android",
        appVersion: "1.0.0",
        lastSeenAt: new Date(now - 3 * 86400_000).toISOString(),
        issuedAt: new Date(now - 60 * 86400_000).toISOString(),
        status: "inactive",
      },
    ];
  },

  async registerPushToken(_input: PushTokenInput): Promise<PushTokenResult> {
    await delay(150);
    return { success: true, registeredAt: new Date().toISOString() };
  },

  async getInbox(): Promise<InboxMessage[]> {
    await delay(120);
    return [];
  },

  async markInboxRead(_input: InboxMarkReadInput): Promise<{ success: boolean }> {
    await delay(80);
    return { success: true };
  },

  async deleteInbox(input: InboxDeleteInput): Promise<{ success: boolean; deleted: number }> {
    await delay(100);
    return { success: true, deleted: input.all ? 3 : input.ids?.length ?? 0 };
  },

  async getInboxMessage(id: string): Promise<InboxMessage> {
    await delay(80);
    return {
      id,
      category: "system",
      title: "Demo bildirim",
      body: "Mock detay içeriği.",
      createdAtUtc: new Date().toISOString(),
      read: true,
    };
  },

  async getNotificationPrefs(): Promise<NotificationPrefs> {
    await delay(80);
    return {
      master: true,
      reminders: true,
      collections: true,
      debts: true,
      system: true,
    };
  },

  async updateNotificationPrefs(prefs: NotificationPrefs): Promise<NotificationPrefs> {
    await delay(100);
    return prefs;
  },
};

export type NsxApi = typeof mockApi;
