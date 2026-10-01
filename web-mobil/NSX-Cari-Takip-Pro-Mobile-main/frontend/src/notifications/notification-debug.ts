/** Development diagnostics: no customer text, credentials or token values. */
export function traceNotificationTap(stage: string, details: {
  id?: string;
  category?: string;
  appState?: string | null;
  found?: boolean;
} = {}): void {
  if (typeof __DEV__ !== "undefined" && __DEV__) {
    console.info(`[NSX Notification] ${stage}`, JSON.stringify(details));
  }
}
