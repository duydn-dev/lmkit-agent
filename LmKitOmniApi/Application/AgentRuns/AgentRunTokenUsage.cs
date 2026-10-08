using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.AI;

namespace LmKitOmniApi.Application.AgentRuns;

/// <summary>
/// Ước lượng chi phí token của MỘT lượt chạy agent, dùng chung cho lượt chạy đầu
/// (<c>StreamAgentRunCommandHandler</c>) và mọi lần chạy lại sau phê duyệt
/// (<c>AgentRunResumeService</c>) — hai đường đó phải cộng ra cùng một con số, nên công
/// thức nằm ở đây chứ không lặp lại hai nơi.
///
/// <para><b>Đây là ƯỚC LƯỢNG, không phải usage của model.</b> Cùng bộ đếm tiktoken với
/// lượt chat (<see cref="ITokenManagementService.EstimateTokenCount"/>) nên cộng được vào
/// cùng một dashboard và đổi chiều được với nhau; nhưng nó KHÔNG thấy được system prompt
/// và schema công cụ mà orchestrator tự dựng, nên luôn nhỏ hơn chi phí thật.</para>
///
/// <para><b>Vì sao tính theo bước.</b> Vòng ReAct gửi lại toàn bộ tích lũy mỗi lượt: mỗi
/// observation của tool được nạp lại vào lượt suy luận kế tiếp, còn mỗi payload gọi tool là
/// thứ model TỰ SINH ra. Bỏ qua bước sẽ đếm thiếu đúng phần đắt nhất của một lần chạy (một
/// phiên nhiều bước tốn gấp nhiều lần một lượt chat), nên hai vế được cộng riêng:</para>
/// <list type="bullet">
/// <item>prompt = câu lệnh/goal + tổng observation của mọi bước.</item>
/// <item>completion = tổng input của mọi bước (payload gọi tool) + câu trả lời cuối.</item>
/// </list>
/// </summary>
public static class AgentRunTokenUsage
{
    /// <param name="PromptTokens">Token ước lượng của phần gửi LÊN model.</param>
    /// <param name="CompletionTokens">Token ước lượng của phần model SINH ra.</param>
    public readonly record struct Usage(int PromptTokens, int CompletionTokens);

    public static Usage Estimate(
        ITokenManagementService tokens,
        string prompt,
        IReadOnlyList<AgentRunStepData> steps,
        string finalText)
    {
        var promptTokens = tokens.EstimateTokenCount(prompt);
        var completionTokens = string.IsNullOrEmpty(finalText) ? 0 : tokens.EstimateTokenCount(finalText);

        foreach (var step in steps)
        {
            // Bước mà orchestrator ghi khi hàng đợi suy luận TỪ CHỐI lần chạy không phải một
            // lượt suy luận: model chưa hề chạy, nên tính nó vào chi phí là bịa.
            if (step.Action == AgentRunStepData.AdmissionRefusedAction) continue;

            if (!string.IsNullOrEmpty(step.Observation))
            {
                promptTokens += tokens.EstimateTokenCount(step.Observation);
            }

            // Input của bước là payload tool do model sinh — tính vào completion, cùng chỗ
            // với câu trả lời cuối. Bước bị từ chối vì hết chỗ suy luận không có input.
            if (!string.IsNullOrEmpty(step.Input))
            {
                completionTokens += tokens.EstimateTokenCount(step.Input);
            }
        }

        return new Usage(promptTokens, completionTokens);
    }

    /// <summary>
    /// Lần chạy này có thật sự gọi model lần nào chưa. Handler và service dùng nó để KHÔNG ghi
    /// chi phí cho một lần bị hàng đợi từ chối hết chỗ — nếu không, mỗi lần bị từ chối lại làm
    /// dashboard đội thêm một khoản token chưa từng tồn tại.
    /// </summary>
    public static bool HasModelWork(IReadOnlyList<AgentRunStepData> steps, bool completed, bool awaitingApproval)
        => completed
            || awaitingApproval
            || steps.Any(step => step.Action != AgentRunStepData.AdmissionRefusedAction);

    /// <summary>Wraps <see cref="HasModelWork(IReadOnlyList{AgentRunStepData}, bool, bool)"/> for a completed final pass.</summary>
    public static bool HasModelWork(string rawContent, IReadOnlyList<AgentRunStepData> steps)
        => HasModelWork(steps, completed: !string.IsNullOrEmpty(rawContent), awaitingApproval: false);
}
