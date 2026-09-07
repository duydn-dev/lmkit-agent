import { describe, expect, it } from 'vitest';
import { agentRunStatusMeta, KNOWN_AGENT_RUN_STATUSES } from './agentRunStatus';

/**
 * Mirrors `LmKitOmniApi/Application/AgentRuns/AgentRunStatuses.cs`. If the API adds
 * a status, this list is where the client learns about it — the "every status is
 * readable" test below then fails until a Vietnamese label exists for it.
 */
const API_STATUSES = [
  'Running',
  'Completed',
  'Failed',
  'AwaitingApproval',
  'CompletedAfterApproval',
  'Rejected',
  'Expired'
];

describe('agentRunStatusMeta', () => {
  it('covers exactly the API vocabulary', () => {
    expect([...KNOWN_AGENT_RUN_STATUSES].sort()).toEqual([...API_STATUSES].sort());
  });

  it('renders every status as Vietnamese, never the raw identifier', () => {
    for (const status of API_STATUSES) {
      const meta = agentRunStatusMeta(status);
      expect(meta.label).not.toBe(status);
      expect(meta.label.trim()).not.toBe('');
      expect(meta.description.trim()).not.toBe('');
      expect(meta.classes).toContain('border-');
    }
  });

  it('gives every status its own label and its own pill colour', () => {
    const labels = API_STATUSES.map((status) => agentRunStatusMeta(status).label);
    const classes = API_STATUSES.map((status) => agentRunStatusMeta(status).classes);
    expect(new Set(labels).size).toBe(API_STATUSES.length);
    expect(new Set(classes).size).toBe(API_STATUSES.length);
  });

  it('keeps "nobody answered" distinct from "a human said no"', () => {
    // Expired and Rejected both end a run without executing the action, but only
    // one of them is a decision — the labels must not blur that.
    const expired = agentRunStatusMeta('Expired');
    const rejected = agentRunStatusMeta('Rejected');
    expect(expired.label).not.toBe(rejected.label);
    expect(expired.description).toContain('KHÔNG được thực thi');
    expect(rejected.description).toContain('từ chối');
  });

  it('marks the two resting states as non-terminal and the rest as terminal', () => {
    expect(agentRunStatusMeta('Running').terminal).toBe(false);
    expect(agentRunStatusMeta('AwaitingApproval').terminal).toBe(false);
    for (const status of ['Completed', 'Failed', 'CompletedAfterApproval', 'Rejected', 'Expired']) {
      expect(agentRunStatusMeta(status).terminal).toBe(true);
    }
  });

  it('degrades safely for a status this client has never heard of', () => {
    const meta = agentRunStatusMeta('SomethingNew');
    expect(meta.label).toBe('SomethingNew');
    expect(meta.classes).toContain('gray');
    expect(agentRunStatusMeta('').label).toBe('Không rõ');
  });
});
