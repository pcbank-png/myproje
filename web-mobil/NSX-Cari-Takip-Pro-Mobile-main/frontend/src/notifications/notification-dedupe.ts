// Shared by remote push and the realtime local fallback.
const shown = new Set<string>();

export function resetNotificationDedupe(): void {
  shown.clear();
}

export function wasNotificationShown(id?: string | null): boolean {
  return !!id && shown.has(id);
}

export function markNotificationShown(id?: string | null): void {
  if (!id) return;
  shown.add(id);
  if (shown.size > 500) shown.delete(shown.values().next().value!);
}

export function forgetNotificationShown(id: string): void {
  shown.delete(id);
}
