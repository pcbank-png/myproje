import type * as Notifications from "expo-notifications";

/** A stable id is also required for older pushes and local test notifications. */
export function readNotificationContent(notification: Notifications.Notification) {
  const content = notification.request.content;
  const data = content.data as Record<string, unknown> | undefined;
  const text = (value: unknown) => typeof value === "string" ? value.trim() : "";
  const messageId = text(data?.messageId);
  const entityId = text(data?.entityId);
  const date = new Date(notification.date);
  return {
    id: messageId || (entityId ? `push-${entityId}` : `push-${notification.request.identifier}`),
    title: text(content.title) || "NSX Cari",
    body: text(content.body) || "Yeni bir güncelleme var.",
    category: text(data?.category) || undefined,
    createdAt: Number.isFinite(date.getTime()) ? date.toISOString() : new Date().toISOString(),
  };
}

export function notificationResponseKey(response: Notifications.NotificationResponse): string {
  return `${response.notification.request.identifier}:${response.notification.date}:${response.actionIdentifier}`;
}
