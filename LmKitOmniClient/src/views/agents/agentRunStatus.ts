/**
 * Vietnamese presentation for every value of `AgentRunStatuses`
 * (`LmKitOmniApi/Application/AgentRuns/AgentRunStatuses.cs`).
 *
 * The API's vocabulary is closed and each value means something different to the
 * person reading the pill, so all seven are spelled out here. `Rejected`,
 * `Expired` and `CompletedAfterApproval` in particular used to fall through to the
 * default branch and render the raw English identifier in a neutral grey pill —
 * three outcomes that look alike but are not: the action was refused, nobody
 * answered in time, or the action ran and the run stopped without resuming.
 *
 * An unknown status still degrades safely (its own identifier, neutral pill) so a
 * status added on the server can never blank the UI.
 */
export interface AgentRunStatusMeta {
  /** Label inside the pill. */
  label: string;
  /** Tailwind classes for the pill. */
  classes: string;
  /** One-line explanation, surfaced as the pill's `title`. */
  description: string;
  /** The run has reached a state it will never leave. */
  terminal: boolean;
}

const NEUTRAL = 'bg-gray-50 text-gray-600 border-gray-200';

const META: Record<string, AgentRunStatusMeta> = {
  Running: {
    label: 'Đang chạy',
    classes: 'bg-amber-50 text-amber-900 border-amber-200',
    description: 'Agent đang lập kế hoạch và gọi công cụ.',
    terminal: false
  },
  Completed: {
    label: 'Hoàn tất',
    classes: 'bg-emerald-50 text-emerald-900 border-emerald-200',
    description: 'Agent đã chạy hết vòng lặp và tổng hợp câu trả lời.',
    terminal: true
  },
  AwaitingApproval: {
    label: 'Chờ phê duyệt',
    classes: 'bg-sky-50 text-sky-900 border-sky-200',
    description: 'Agent đã dừng ở một hành động cần bạn phê duyệt trước khi thực thi.',
    terminal: false
  },
  CompletedAfterApproval: {
    label: 'Hoàn tất sau phê duyệt',
    classes: 'bg-teal-50 text-teal-900 border-teal-200',
    description:
      'Hành động đã được phê duyệt và thực thi. Lần chạy kết thúc ở kết quả của công cụ, agent không tiếp tục suy luận thêm.',
    terminal: true
  },
  Rejected: {
    label: 'Đã từ chối',
    classes: 'bg-rose-50 text-rose-900 border-rose-200',
    description: 'Bạn đã từ chối hành động, nên lần chạy dừng lại mà không thực thi nó.',
    terminal: true
  },
  Expired: {
    label: 'Hết hạn phê duyệt',
    classes: 'bg-slate-100 text-slate-700 border-slate-300',
    description:
      'Không ai trả lời yêu cầu phê duyệt trước hạn, nên lần chạy bị đóng lại. Hành động KHÔNG được thực thi.',
    terminal: true
  },
  Failed: {
    label: 'Thất bại',
    classes: 'bg-red-50 text-red-800 border-red-200',
    description: 'Lần chạy bị hủy giữa chừng hoặc gặp lỗi.',
    terminal: true
  }
};

export function agentRunStatusMeta(status: string): AgentRunStatusMeta {
  return META[status] ?? {
    label: status || 'Không rõ',
    classes: NEUTRAL,
    description: 'Trạng thái không xác định.',
    terminal: false
  };
}

/** Statuses this client knows how to present — the API's full vocabulary. */
export const KNOWN_AGENT_RUN_STATUSES = Object.keys(META);
