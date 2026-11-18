// Addresses within the app. The app may be served under a base path (Server__PublicUrl with a
// path, such as https://example.com/greenglow/), and every page sits at that base. So addresses
// in the code are relative to the page ('api/me', never '/api/me'): the browser puts the base in
// front. ESLint refuses absolute ones. These helpers are for the places a relative path can't go.

/** The base path the pages are at: "/" or, say, "/greenglow/". */
export function basePath(): string {
  return new URL('./', location.href).pathname;
}

/** A path relative to the app as a whole address, for a window that isn't this page. */
export function appUrl(path: string): string {
  return new URL(path, location.href).href;
}

/** A WebSocket address for a path relative to the app, such as `ws/user`. */
export function socketUrl(path: string): string {
  const url = new URL(path, location.href);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  return url.href;
}
