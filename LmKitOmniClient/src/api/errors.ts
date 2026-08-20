type ApiProblem = {
  message?: unknown;
  title?: unknown;
  detail?: unknown;
  errors?: unknown;
};

function firstString(value: unknown): string | undefined {
  if (typeof value === 'string' && value.trim()) return value.trim();
  if (Array.isArray(value)) {
    for (const item of value) {
      const result = firstString(item);
      if (result) return result;
    }
  }
  if (value && typeof value === 'object') {
    for (const item of Object.values(value)) {
      const result = firstString(item);
      if (result) return result;
    }
  }
  return undefined;
}

export async function readApiError(response: Response, fallback: string): Promise<string> {
  const fallbackWithStatus = response.status
    ? `${fallback} (${response.status}).`
    : fallback;

  try {
    const contentType = response.headers.get('content-type')?.toLowerCase() ?? '';
    if (contentType.includes('json')) {
      const problem = await response.json() as ApiProblem;
      return firstString(problem.message)
        ?? firstString(problem.detail)
        ?? firstString(problem.errors)
        ?? firstString(problem.title)
        ?? fallbackWithStatus;
    }

    const text = (await response.text()).trim();
    if (text && !/<(?:!doctype|html|body)\b/i.test(text)) return text.slice(0, 500);
  } catch {
    // A malformed or already-consumed error body must not hide the status fallback.
  }

  return fallbackWithStatus;
}

export function errorMessage(cause: unknown, fallback: string): string {
  return cause instanceof Error && cause.message.trim() ? cause.message : fallback;
}
