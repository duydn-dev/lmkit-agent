const BASE_URL = import.meta.env.VITE_API_URL || 'http://localhost:5032';

class Http {
  private prepareHeaders(headers: HeadersInit = {}, isFormData: boolean = false): HeadersInit {
    const reqHeaders: Record<string, string> = { ...headers as Record<string, string> };
    
    // Fetch API requires us to let it set the Content-Type automatically for FormData (with boundary)
    if (!isFormData && !reqHeaders['Content-Type']) {
      reqHeaders['Content-Type'] = 'application/json';
    }
    
    return reqHeaders;
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

  private async _request(url: string, options: RequestInit): Promise<Response> {
    const isRefreshUrl = url === '/api/auth/refresh';
    let response = await fetch(`${BASE_URL}${url}`, options);

    // If 401 and it's not the refresh endpoint itself
    if (response.status === 401 && !isRefreshUrl) {
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
            // If refresh fails, we could redirect to login here
            if (typeof window !== 'undefined') {
               // Let the auth store or component handle it if possible, or force redirect:
               window.location.href = '/login';
            }
          }
        } catch (e) {
          this.isRefreshing = false;
          this.onRefreshed(false);
        }
      } else {
        // Wait for the ongoing refresh to finish
        const success = await new Promise<boolean>((resolve) => {
          this.addRefreshSubscriber(resolve);
        });
        if (success) {
          // Retry the original request
          response = await fetch(`${BASE_URL}${url}`, options);
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

  async post(url: string, body?: any, headers: HeadersInit = {}): Promise<Response> {
    const isFormData = body instanceof FormData;
    return this._request(url, {
      method: 'POST',
      headers: this.prepareHeaders(headers, isFormData),
      body: isFormData ? body : (body ? JSON.stringify(body) : undefined),
      credentials: 'include'
    });
  }
  
  async put(url: string, body?: any, headers: HeadersInit = {}): Promise<Response> {
    const isFormData = body instanceof FormData;
    return this._request(url, {
      method: 'PUT',
      headers: this.prepareHeaders(headers, isFormData),
      body: isFormData ? body : (body ? JSON.stringify(body) : undefined),
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
