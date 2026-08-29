/**
 * API origin shared by every request. Also exported for the few surfaces (the
 * public share page) that must call the API with a PLAIN fetch — no refresh /
 * login-redirect machinery — but still target the same backend.
 */
export const API_BASE_URL: string = import.meta.env.VITE_API_URL || '';

const BASE_URL = API_BASE_URL;

class Http {
  private prepareHeaders(headers: HeadersInit = {}, isFormData: boolean = false): HeadersInit {
    const reqHeaders: Record<string, string> = { ...headers as Record<string, string> };
    
    // Fetch API requires us to let it set the Content-Type automatically for FormData (with boundary)
    if (!isFormData && !reqHeaders['Content-Type']) {
      reqHeaders['Content-Type'] = 'application/json';
    }

    return reqHeaders;
  }

  private serializeBody(body: unknown): BodyInit | undefined {
    if (body instanceof FormData) {
      return body;
    }
    // Preserve the original semantics: falsy bodies (undefined/null/'') are sent without a payload.
    return body ? JSON.stringify(body) : undefined;
  }

  private isRefreshing = false;
  private refreshSubscribers: ((success: boolean) => void)[] = [];

  private onRefreshed(success: boolean) {
    this.refreshSubscribers.forEach((cb) => cb(success));
    this.refreshSubscribers = [];
  }

  private addRefreshSubscriber(cb: (success: boolean) => void) {
    this.refreshSubscribers.push(cb);
  }

  private redirectToLogin() {
    if (typeof window !== 'undefined' && window.location.pathname !== '/login') {
      window.location.assign('/login');
    }
  }

  private async _request(url: string, options: RequestInit): Promise<Response> {
    const isAuthLifecycleUrl = url === '/api/auth/login'
      || url === '/api/auth/refresh'
      || url === '/api/auth/logout';
    let response = await fetch(`${BASE_URL}${url}`, options);

    // If 401 and it's not the refresh endpoint itself
    if (response.status === 401 && !isAuthLifecycleUrl) {
      if (!this.isRefreshing) {
        this.isRefreshing = true;
        
        try {
          // Attempt to refresh
          const refreshRes = await fetch(`${BASE_URL}/api/auth/refresh`, {
            method: 'POST',
            credentials: 'include'
          });
          
          if (refreshRes.ok) {
            this.isRefreshing = false;
            this.onRefreshed(true);
            // Retry the original request
            response = await fetch(`${BASE_URL}${url}`, options);
          } else {
            this.isRefreshing = false;
            this.onRefreshed(false);
            // Refresh was rejected: send the user back to the login screen.
            this.redirectToLogin();
          }
        } catch {
          // A network error while refreshing is also a refresh failure.
          this.isRefreshing = false;
          this.onRefreshed(false);
          this.redirectToLogin();
        }
      } else {
        // Wait for the ongoing refresh to finish
        const success = await new Promise<boolean>((resolve) => {
          this.addRefreshSubscriber(resolve);
        });
        if (success) {
          // Retry the original request
          response = await fetch(`${BASE_URL}${url}`, options);
        } else {
          // The shared refresh failed for this waiter too. Previously the waiter
          // branch silently returned the original 401 without redirecting; redirect
          // so every concurrent request lands on /login consistently.
          this.redirectToLogin();
        }
      }
    }

    return response;
  }

  async get(url: string, headers: HeadersInit = {}): Promise<Response> {
    return this._request(url, {
      method: 'GET',
      headers: this.prepareHeaders(headers, false),
      credentials: 'include'
    });
  }

  async post(url: string, body?: unknown, headers: HeadersInit = {}): Promise<Response> {
    const isFormData = body instanceof FormData;
    return this._request(url, {
      method: 'POST',
      headers: this.prepareHeaders(headers, isFormData),
      body: this.serializeBody(body),
      credentials: 'include'
    });
  }

  async put(url: string, body?: unknown, headers: HeadersInit = {}): Promise<Response> {
    const isFormData = body instanceof FormData;
    return this._request(url, {
      method: 'PUT',
      headers: this.prepareHeaders(headers, isFormData),
      body: this.serializeBody(body),
      credentials: 'include'
    });
  }

  async patch(url: string, body?: unknown, headers: HeadersInit = {}): Promise<Response> {
    const isFormData = body instanceof FormData;
    return this._request(url, {
      method: 'PATCH',
      headers: this.prepareHeaders(headers, isFormData),
      body: this.serializeBody(body),
      credentials: 'include'
    });
  }

  async delete(url: string, headers: HeadersInit = {}): Promise<Response> {
    return this._request(url, {
      method: 'DELETE',
      headers: this.prepareHeaders(headers, false),
      credentials: 'include'
    });
  }
}

export const http = new Http();
