// Calls to the server's admin API. Errors carry the server's message, so the page can show
// it as it is.

export class ApiError extends Error {
  readonly status: number;
  readonly code: string | undefined;
  /** The error's whole body, for errors that carry more than a message. */
  readonly data: unknown;

  constructor(status: number, message: string, code?: string, data?: unknown) {
    super(message);
    this.status = status;
    this.code = code;
    this.data = data;
  }
}

export async function api<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    headers: body === undefined ? {} : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (response.status === 401) {
    location.assign(`/auth/signin?returnUrl=${encodeURIComponent(location.pathname)}`);
    throw new ApiError(401, 'Signing in…');
  }
  if (!response.ok) {
    let message = `${method} ${path} failed (${response.status})`;
    let code: string | undefined;
    let data: unknown;
    try {
      const error = (await response.json()) as { message?: string; error?: string };
      message = error.message ?? message;
      code = error.error;
      data = error;
    } catch {
      // No JSON body: keep the status.
    }
    throw new ApiError(response.status, message, code, data);
  }
  return (response.status === 204 ? undefined : await response.json()) as T;
}

/** A person or group from the directory. */
export interface DirectoryEntry {
  id: string;
  name: string;
  upn: string | null;
}
