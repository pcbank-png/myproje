// Deterministic mock seed data for the NSX Mobile preview build.
// This file is loaded once by the mock store; all state changes happen
// in memory. When the real NSX .NET API is available, flip
// EXPO_PUBLIC_NSX_MODE=live and this file becomes dead code.

import type { Company, Customer, Transaction } from "./types";

const now = new Date();
const daysAgo = (n: number, hours = 10) => {
  const d = new Date(now);
  d.setDate(d.getDate() - n);
  d.setHours(hours, 0, 0, 0);
  return d.toISOString();
};

export const seedCompanies: Company[] = [
  { id: "c-akyildiz", name: "Akyıldız Ticaret Ltd. Şti.", taxNumber: "1234567890", isActive: true },
  { id: "c-mavikale", name: "Mavi Kale Gıda A.Ş.", taxNumber: "9876543210", isActive: true },
];

const AK = "c-akyildiz";

export const seedCustomers: Customer[] = [
  { id: "m-01", companyId: AK, name: "Yılmaz Metal Sanayi", phone: "0532 111 22 33", email: "info@yilmazmetal.com", netBalance: 0, version: 1, updatedAt: daysAgo(0) },
  { id: "m-02", companyId: AK, name: "Deniz Nakliye", phone: "0555 444 55 66", netBalance: 0, version: 1, updatedAt: daysAgo(0) },
  { id: "m-03", companyId: AK, name: "Kaya İnşaat", phone: "0533 222 88 99", note: "Aylık ödeme yapıyor", netBalance: 0, version: 1, updatedAt: daysAgo(0) },
  { id: "m-04", companyId: AK, name: "Bahar Tekstil", phone: "0505 777 33 44", netBalance: 0, version: 1, updatedAt: daysAgo(0) },
  { id: "m-05", companyId: AK, name: "Ege Zeytinyağı", phone: "0542 888 11 22", netBalance: 0, version: 1, updatedAt: daysAgo(0) },
  { id: "m-06", companyId: AK, name: "Anadolu Kırtasiye", phone: "0531 555 99 00", netBalance: 0, version: 1, updatedAt: daysAgo(0) },
  { id: "m-07", companyId: AK, name: "Marmara Cam", phone: "0537 666 22 11", netBalance: 0, version: 1, updatedAt: daysAgo(0) },
  { id: "m-08", companyId: AK, name: "Toros Petrol", phone: "0544 333 44 55", netBalance: 0, version: 1, updatedAt: daysAgo(0) },
  { id: "m-09", companyId: "c-mavikale", name: "Karadeniz Balık", phone: "0538 999 11 22", netBalance: 0, version: 1, updatedAt: daysAgo(0) },
  { id: "m-10", companyId: "c-mavikale", name: "İzmir Un Fabrikası", phone: "0530 222 33 44", netBalance: 0, version: 1, updatedAt: daysAgo(0) },
];

// Raw ledger entries; net balances are recomputed by the mock store.
export const seedTransactions: Transaction[] = [
  { id: "t-01", companyId: AK, customerId: "m-01", customerName: "Yılmaz Metal Sanayi", kind: "debt", amount: 45000, description: "Fatura #A-2401", date: daysAgo(28), createdAt: daysAgo(28), version: 1 },
  { id: "t-02", companyId: AK, customerId: "m-01", customerName: "Yılmaz Metal Sanayi", kind: "collection", amount: 15000, description: "Havale", date: daysAgo(14), createdAt: daysAgo(14), version: 1 },
  { id: "t-03", companyId: AK, customerId: "m-01", customerName: "Yılmaz Metal Sanayi", kind: "debt", amount: 22500, description: "Fatura #A-2415", date: daysAgo(7), createdAt: daysAgo(7), version: 1 },

  { id: "t-04", companyId: AK, customerId: "m-02", customerName: "Deniz Nakliye", kind: "debt", amount: 18750, description: "Sevkiyat bedeli", date: daysAgo(21), createdAt: daysAgo(21), version: 1 },
  { id: "t-05", companyId: AK, customerId: "m-02", customerName: "Deniz Nakliye", kind: "collection", amount: 18750, description: "Nakit ödeme", date: daysAgo(10), createdAt: daysAgo(10), version: 1 },

  { id: "t-06", companyId: AK, customerId: "m-03", customerName: "Kaya İnşaat", kind: "debt", amount: 120000, description: "Malzeme sevkiyatı", date: daysAgo(45), createdAt: daysAgo(45), version: 1 },
  { id: "t-07", companyId: AK, customerId: "m-03", customerName: "Kaya İnşaat", kind: "collection", amount: 40000, description: "1. taksit", date: daysAgo(30), createdAt: daysAgo(30), version: 1 },
  { id: "t-08", companyId: AK, customerId: "m-03", customerName: "Kaya İnşaat", kind: "collection", amount: 40000, description: "2. taksit", date: daysAgo(15), createdAt: daysAgo(15), version: 1 },

  { id: "t-09", companyId: AK, customerId: "m-04", customerName: "Bahar Tekstil", kind: "debt", amount: 8500, description: "Kumaş", date: daysAgo(5), createdAt: daysAgo(5), version: 1 },

  { id: "t-10", companyId: AK, customerId: "m-05", customerName: "Ege Zeytinyağı", kind: "debt", amount: 34200, description: "Toptan sipariş", date: daysAgo(18), createdAt: daysAgo(18), version: 1 },
  { id: "t-11", companyId: AK, customerId: "m-05", customerName: "Ege Zeytinyağı", kind: "collection", amount: 34200, description: "Havale", date: daysAgo(3), createdAt: daysAgo(3), version: 1 },

  { id: "t-12", companyId: AK, customerId: "m-06", customerName: "Anadolu Kırtasiye", kind: "debt", amount: 6250, description: "Ofis malzemeleri", date: daysAgo(9), createdAt: daysAgo(9), version: 1 },
  { id: "t-13", companyId: AK, customerId: "m-06", customerName: "Anadolu Kırtasiye", kind: "collection", amount: 2000, description: "Kısmi ödeme", date: daysAgo(4), createdAt: daysAgo(4), version: 1 },

  { id: "t-14", companyId: AK, customerId: "m-07", customerName: "Marmara Cam", kind: "debt", amount: 27800, description: "Cam sipariş", date: daysAgo(12), createdAt: daysAgo(12), version: 1 },
  { id: "t-15", companyId: AK, customerId: "m-08", customerName: "Toros Petrol", kind: "collection", amount: 9500, description: "Peşin ödeme", date: daysAgo(2), createdAt: daysAgo(2), version: 1 },
  { id: "t-16", companyId: AK, customerId: "m-08", customerName: "Toros Petrol", kind: "debt", amount: 15000, description: "Yakıt", date: daysAgo(1), createdAt: daysAgo(1), version: 1 },

  { id: "t-17", companyId: "c-mavikale", customerId: "m-09", customerName: "Karadeniz Balık", kind: "debt", amount: 12300, description: "Soğuk zincir", date: daysAgo(6), createdAt: daysAgo(6), version: 1 },
  { id: "t-18", companyId: "c-mavikale", customerId: "m-10", customerName: "İzmir Un Fabrikası", kind: "collection", amount: 55000, description: "Toplu tahsilat", date: daysAgo(8), createdAt: daysAgo(8), version: 1 },
];
