/**
 * Formats a date-ish value as a vi-VN short date (2-digit day/month, numeric
 * year). Nullish or unparseable input yields an empty string.
 *
 * Shared verbatim by AgentsView, ProjectsView, ApiKeysView and DocumentView,
 * which all render backend ISO timestamps exactly this way.
 */
export function formatDate(value: string | number | Date | null | undefined): string {
  const date = new Date(value ?? NaN);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleDateString('vi-VN', { year: 'numeric', month: '2-digit', day: '2-digit' });
}
