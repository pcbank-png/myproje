// Realtime bağlantı durumu — query-client'ın polling kararı okur.
// Leaf modül: hiçbir şey import etmez (cycle yok).

let connected = false;

export function setRealtimeConnected(v: boolean): void {
  connected = v;
}

export function isRealtimeConnected(): boolean {
  return connected;
}
