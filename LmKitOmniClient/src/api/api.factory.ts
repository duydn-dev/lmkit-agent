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
  },
  AGENTS: {
    CUSTOM: '/api/agents/custom',
    CUSTOM_BY_ID: (id: string) => `/api/agents/custom/${id}`,
    TOOL_CATALOG: '/api/agents/custom/tools'
  },
  CANVAS: {
    LIST: (sessionId?: string) => sessionId ? `/api/canvas?sessionId=${sessionId}` : '/api/canvas',
    CREATE: '/api/canvas',
    BY_ROOT: (rootId: string) => `/api/canvas/${rootId}`,
    VERSION: (rootId: string, version: number) => `/api/canvas/${rootId}?version=${version}`,
    VERSIONS: (rootId: string) => `/api/canvas/${rootId}/versions`
  },
  RESEARCH: {
    RUN: '/api/research'
  },
  SCHEDULES: {
    BASE: '/api/schedules',
    BY_ID: (id: string) => `/api/schedules/${id}`,
    TOGGLE: (id: string) => `/api/schedules/${id}/toggle`
  },
  NOTIFICATIONS: {
    LIST: (unreadOnly = false) => unreadOnly ? '/api/notifications?unreadOnly=true' : '/api/notifications',
    MARK_READ: (id: string) => `/api/notifications/${id}/read`,
    READ_ALL: '/api/notifications/read-all'
  },
  SPEECH: {
    TRANSCRIBE_UPLOAD: '/api/speech/transcribe-upload'
  },
  PROJECTS: {
    BASE: '/api/projects',
    BY_ID: (id: string) => `/api/projects/${id}`,
    SESSIONS: (id: string) => `/api/projects/${id}/sessions`
  },
  APIKEYS: {
    BASE: '/api/api-keys',
    BY_ID: (id: string) => `/api/api-keys/${id}`
  },
  MCP: {
    CATALOG: '/api/mcp-servers/catalog'
  }
};
