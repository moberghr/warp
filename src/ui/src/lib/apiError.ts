// Reading a failed dashboard API call: the message the server gave, and whether it was a 404. Shared by every page that
// shows a mutation or read error, so a server's own explanation is never replaced by a generic one.

/**
 * Extracts the message from an axios-style error. The dashboard API answers a 400/404/503 with `{ error }` (an upsert
 * whose row was deleted meanwhile answers 404 with one too); a validation 400 may be a plain string or ProblemDetails.
 * When the server answered without a message, `fallback` (each caller names its own action) beats axios's generic
 * "Request failed with status code …"; with no answer at all, the transport's message ("Network Error") is shown.
 */
export function errorMessage(error: unknown, fallback = 'The request failed.'): string {
  const response = (error as { response?: { data?: unknown } } | null)?.response;
  const data = response?.data;
  if (typeof data === 'string' && data.trim() !== '') {
    return data;
  }
  if (data && typeof data === 'object') {
    const body = data as { error?: unknown; detail?: unknown; title?: unknown };
    if (typeof body.error === 'string' && body.error) {
      return body.error;
    }
    if (typeof body.detail === 'string' && body.detail) {
      return body.detail;
    }
    if (typeof body.title === 'string' && body.title) {
      return body.title;
    }
  }

  return response === undefined && error instanceof Error && error.message ? error.message : fallback;
}

/** The HTTP status of an axios-style error, or null when the request never got an answer. */
export function errorStatus(error: unknown): number | null {
  const status = (error as { response?: { status?: unknown } } | null)?.response?.status;

  return typeof status === 'number' ? status : null;
}
