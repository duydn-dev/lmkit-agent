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

  async get(url: string, headers: HeadersInit = {}): Promise<Response> {
    return fetch(`${BASE_URL}${url}`, {
      method: 'GET',
      headers: this.prepareHeaders(headers, false),
      credentials: 'include'
    });
  }

  async post(url: string, body?: any, headers: HeadersInit = {}): Promise<Response> {
    const isFormData = body instanceof FormData;
    return fetch(`${BASE_URL}${url}`, {
      method: 'POST',
      headers: this.prepareHeaders(headers, isFormData),
      body: isFormData ? body : (body ? JSON.stringify(body) : undefined),
      credentials: 'include'
    });
  }
  
  async put(url: string, body?: any, headers: HeadersInit = {}): Promise<Response> {
    const isFormData = body instanceof FormData;
    return fetch(`${BASE_URL}${url}`, {
      method: 'PUT',
      headers: this.prepareHeaders(headers, isFormData),
      body: isFormData ? body : (body ? JSON.stringify(body) : undefined),
      credentials: 'include'
    });
  }

  async delete(url: string, headers: HeadersInit = {}): Promise<Response> {
    return fetch(`${BASE_URL}${url}`, {
      method: 'DELETE',
      headers: this.prepareHeaders(headers, false),
      credentials: 'include'
    });
  }
}

export const http = new Http();
