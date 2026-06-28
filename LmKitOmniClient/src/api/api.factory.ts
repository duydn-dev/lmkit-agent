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
    GET_MESSAGES: (id: string) => `/api/chat/sessions/${id}/messages`
  },
  DOCUMENT: {
    BASE: '/api/document',
    UPLOAD: '/api/document/upload'
  }
};
