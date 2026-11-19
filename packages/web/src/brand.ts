// The product's name as this server brands it. An administrator can rename GreenGlow on the
// admin page (Branding); the server then serves every page with the new name in its meta tags,
// so text made by scripts reads it from there rather than saying GreenGlow.

function meta(name: string): string | undefined {
  if (typeof document === 'undefined') return undefined;
  const content = document.querySelector<HTMLMetaElement>(`meta[name="${name}"]`)?.content.trim();
  return content || undefined;
}

/** The full name, such as GreenGlow Connect. */
export function brandName(): string {
  return meta('application-name') ?? 'GreenGlow Connect';
}

/** The short name, such as GreenGlow: what messages call the product. */
export function brandShortName(): string {
  return meta('apple-mobile-web-app-title') ?? 'GreenGlow';
}
