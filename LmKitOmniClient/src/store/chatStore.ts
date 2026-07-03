import { defineStore } from 'pinia';
import { ref } from 'vue';

export const useChatStore = defineStore('chat', () => {
  const currentSessionId = ref<string | null>(null);
  
  const setSessionId = (id: string | null) => {
    currentSessionId.value = id;
  };

  return {
    currentSessionId,
    setSessionId
  };
});
