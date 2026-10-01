// QR payload → pairing code extraction.
// The NSX desktop issues QR codes that either embed the raw pairing code
// or a login URL carrying it (e.g. https://host/CaritakipCloud/qr?token=NSX-1234).
// Live and mock clients share this parser so the flow is identical.

export function extractPairingCode(raw: string): string | null {
  const data = raw.trim();
  if (!data) return null;

  // Raw short code path: short, single token, no spaces.
  if (data.length <= 64 && !data.includes(" ") && !data.includes("://")) {
    return data;
  }

  // URL path
  try {
    const url = new URL(data);
    const fromQuery =
      url.searchParams.get("token") ??
      url.searchParams.get("code") ??
      url.searchParams.get("pair");
    if (fromQuery) return fromQuery;
    const segments = url.pathname.split("/").filter(Boolean);
    const last = segments[segments.length - 1];
    if (last && last.length >= 4) return decodeURIComponent(last);
  } catch {
    // not a URL, fall through
  }

  // Long opaque payload — treat as the code itself if sane.
  if (data.length <= 256) return data;
  return null;
}
