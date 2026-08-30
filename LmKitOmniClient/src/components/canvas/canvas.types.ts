/**
 * DTO shapes for the Canvas artifact API (see ApiFactory.CANVAS). These mirror
 * the backend contract exactly and are shared between CanvasPanel and ChatView
 * (which only needs the summary for its header count badge).
 */

export type CanvasKind = 'markdown' | 'code' | 'text';

export interface CanvasArtifactSummary {
  id: string;
  rootId: string;
  title: string;
  kind: string;
  language: string | null;
  version: number;
  chatSessionId: string | null;
  updatedAt: string;
}

/** Returned by GET BY_ROOT / VERSION / POST CREATE: summary plus the body. */
export interface CanvasArtifactDetail extends CanvasArtifactSummary {
  content: string;
}

export interface CanvasVersionInfo {
  id: string;
  version: number;
  createdAt: string;
}

/** Payload for CanvasPanel.createFromChat (the "Mở trong Canvas" flow). */
export interface CanvasCreateFromChatPayload {
  title: string;
  kind: CanvasKind;
  language: string | null;
  content: string;
}
