export const ApiFactory = {
  AUTH: {
    LOGIN: '/api/auth/login'
  },
  CHAT: {
    STREAM: '/api/chat/stream',
    STREAM_WITH_FILES: '/api/chat/stream-with-files',
    SESSIONS: '/api/chat/sessions',
    CREATE_SESSION: '/api/chat/sessions',
    DELETE_SESSION: (id: string) => `/api/chat/sessions/${id}`,
    RENAME_SESSION: (id: string) => `/api/chat/sessions/${id}`,
    SEARCH_SESSIONS: (query: string) => `/api/chat/sessions/search?q=${encodeURIComponent(query)}`,
    GET_MESSAGES: (id: string) => `/api/chat/sessions/${id}/messages`
  },
  SHARE: {
    CREATE_LINK: (sessionId: string) => `/api/share/chat-sessions/${sessionId}`,
    REVOKE_LINK: (sessionId: string) => `/api/share/chat-sessions/${sessionId}`,
    // PUBLIC endpoint (no auth): fetched with a plain `fetch`, never via `http`.
    GET_SHARED_CHAT: (token: string) => `/api/share/chat/${encodeURIComponent(token)}`
  },
  DOCUMENT: {
    BASE: '/api/document',
    UPLOAD: '/api/document/upload'
  }
};
