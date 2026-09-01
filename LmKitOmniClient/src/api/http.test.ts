import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { http } from './http';

// --- Test helpers -----------------------------------------------------------

/** A promise whose resolution we control, used to hold a refresh in-flight. */
function deferred<T>() {
  let resolve: (value: T) => void = () => {};
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

const jsonHeaders = { 'content-type': 'application/json' };

const ok = (body: unknown = {}): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: jsonHeaders });

const unauthorized = (): Response =>
  new Response(JSON.stringify({ message: 'unauthorized' }), { status: 401, headers: jsonHeaders });

type FetchHandler = (
  url: string,
  init: RequestInit | undefined,
  callIndex: number
) => Response | Promise<Response>;

/** Installs a fetch stub, recording every call so tests can assert on them. */
function installFetch(handler: FetchHandler) {
  const calls: Array<{ url: string; init?: RequestInit }> = [];
  const fetchMock = vi.fn((url: string, init?: RequestInit) => {
    calls.push({ url, init });
    return Promise.resolve(handler(url, init, calls.length - 1));
  });
  vi.stubGlobal('fetch', fetchMock);
  return {
    calls,
    fetchMock,
    countFor(url: string) {
      return calls.filter((call) => call.url === url).length;
    }
  };
}

/** Yields to the macrotask queue so all pending microtasks settle. */
const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

const REFRESH_URL = '/api/auth/refresh';

// The `http` singleton keeps refresh state (single-flight guard + subscriber
// queue) across calls; reset it so each test starts from a clean slate.
type HttpInternals = {
  isRefreshing: boolean;
  refreshSubscribers: Array<(success: boolean) => void>;
};

let assignMock: ReturnType<typeof vi.fn>;
let locationStub: { pathname: string; assign: ReturnType<typeof vi.fn> };

beforeEach(() => {
  assignMock = vi.fn();
  locationStub = { pathname: '/documents', assign: assignMock };
  vi.stubGlobal('window', { location: locationStub });

  const internals = http as unknown as HttpInternals;
  internals.isRefreshing = false;
  internals.refreshSubscribers = [];
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

// --- Tests ------------------------------------------------------------------

describe('http auth lifecycle', () => {
  it('returns a successful response directly, without refreshing', async () => {
    const net = installFetch(() => ok({ hello: 'world' }));

    const response = await http.get('/api/data');

    expect(response.status).toBe(200);
    expect(net.fetchMock).toHaveBeenCalledTimes(1);
    expect(net.countFor(REFRESH_URL)).toBe(0);
    expect(assignMock).not.toHaveBeenCalled();

    // GET is sent with cookies and a JSON content type.
    expect(net.calls[0].init?.method).toBe('GET');
    expect(net.calls[0].init?.credentials).toBe('include');
  });

  it('does not attempt a refresh when an auth-lifecycle endpoint returns 401', async () => {
    const net = installFetch(() => unauthorized());

    const response = await http.post('/api/auth/login', { email: 'a@b.c', password: 'secret' });

    expect(response.status).toBe(401);
    expect(net.fetchMock).toHaveBeenCalledTimes(1);
    expect(net.countFor(REFRESH_URL)).toBe(0);
    expect(assignMock).not.toHaveBeenCalled();
  });

  it('refreshes once, then retries the original request after success', async () => {
    let dataCalls = 0;
    const net = installFetch((url) => {
      if (url === REFRESH_URL) return ok();
      if (url === '/api/data') {
        dataCalls += 1;
        return dataCalls === 1 ? unauthorized() : ok({ retried: true });
      }
      return ok();
    });

    const response = await http.post('/api/data', { q: 'x' });

    expect(response.status).toBe(200);
    expect(dataCalls).toBe(2); // original (401) + retry (200)
    expect(net.countFor(REFRESH_URL)).toBe(1);
    expect(assignMock).not.toHaveBeenCalled();

    // The retry re-sent the same method and serialized body.
    const dataRequests = net.calls.filter((call) => call.url === '/api/data');
    expect(dataRequests).toHaveLength(2);
    expect(dataRequests[1].init?.method).toBe('POST');
    expect(dataRequests[1].init?.body).toBe(JSON.stringify({ q: 'x' }));
  });

  it('performs exactly ONE refresh for concurrent 401s (single-flight)', async () => {
    const refresh = deferred<Response>();
    let protectedCount = 0;
    const net = installFetch((url) => {
      if (url === REFRESH_URL) return refresh.promise;
      if (url === '/api/protected') {
        protectedCount += 1;
        // The two initial requests get 401; the two retries succeed.
        return protectedCount <= 2 ? unauthorized() : ok();
      }
      return ok();
    });

    const first = http.get('/api/protected');
    const second = http.get('/api/protected');

    // Let both initial requests receive their 401 and settle into
    // leader (refreshing) + waiter (queued) roles.
    await flush();
    expect(net.countFor(REFRESH_URL)).toBe(1);

    // Release the shared refresh; both waiters proceed to retry.
    refresh.resolve(ok());
    const [firstResponse, secondResponse] = await Promise.all([first, second]);

    expect(firstResponse.status).toBe(200);
    expect(secondResponse.status).toBe(200);
    // Still exactly one refresh even though two requests hit 401 concurrently.
    expect(net.countFor(REFRESH_URL)).toBe(1);
    expect(assignMock).not.toHaveBeenCalled();
  });

  it('redirects to /login when the refresh attempt fails', async () => {
    const net = installFetch(() => unauthorized());

    const response = await http.get('/api/data');

    expect(response.status).toBe(401);
    expect(net.countFor(REFRESH_URL)).toBe(1);
    expect(assignMock).toHaveBeenCalledWith('/login');
  });

  it('redirects EVERY waiter to /login when a concurrent refresh fails', async () => {
    const refresh = deferred<Response>();
    const net = installFetch((url) => {
      if (url === REFRESH_URL) return refresh.promise;
      return unauthorized();
    });

    const first = http.get('/api/protected');
    const second = http.get('/api/protected');

    await flush();
    expect(net.countFor(REFRESH_URL)).toBe(1);

    // The shared refresh fails.
    refresh.resolve(unauthorized());
    const [firstResponse, secondResponse] = await Promise.all([first, second]);

    // Neither request is retried; both keep their original 401.
    expect(firstResponse.status).toBe(401);
    expect(secondResponse.status).toBe(401);
    // Leader AND waiter both redirect (regression guard: the waiter used to
    // silently return its 401 without redirecting).
    expect(assignMock).toHaveBeenCalledWith('/login');
    expect(assignMock).toHaveBeenCalledTimes(2);
  });

  it('redirects to /login when the refresh request throws (network error)', async () => {
    const net = installFetch((url) => {
      if (url === REFRESH_URL) return Promise.reject(new Error('network down'));
      return unauthorized();
    });

    const response = await http.get('/api/data');

    expect(response.status).toBe(401);
    expect(net.countFor(REFRESH_URL)).toBe(1);
    expect(assignMock).toHaveBeenCalledWith('/login');
  });

  it('does not redirect when already on the login page', async () => {
    locationStub.pathname = '/login';
    installFetch(() => unauthorized());

    await http.get('/api/data');

    expect(assignMock).not.toHaveBeenCalled();
  });
});
