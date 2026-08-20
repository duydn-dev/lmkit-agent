/**
 * Formats the deliberately small markdown subset supported by the chat UI.
 * User/model content is escaped before any markup is introduced, so callers
 * can safely bind the result with v-html.
 */
export function formatSafeMessage(value: string): string {
  const escaped = (value || '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');

  return escaped
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\r?\n/g, '<br>');
}
