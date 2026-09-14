using System.Text.RegularExpressions;

namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>Ngôn ngữ mà guard hiểu; mỗi ngôn ngữ có bộ quy tắc riêng.</summary>
public enum GuardedCodeLanguage
{
    JavaScript,
    Python
}

/// <summary>
/// Tầng QUY TẮC TĨNH chặn mã nguy hiểm TRƯỚC khi vào sandbox — chạy cho cả
/// run_javascript (Jint) lẫn run_python (container). Chặn ba nhóm hành vi:
/// leo thang quyền hạn (gọi tiến trình/đổi định danh/CLR), dò bí mật
/// (biến môi trường, tệp hệ thống nhạy cảm), và thao tác dữ liệu ngoài luồng
/// (driver CSDL, thư viện mạng) — thao tác CSDL bắt buộc đi qua tool
/// run_database_query (chỉ SELECT + transaction chỉ-đọc) hoặc
/// run_database_write (luôn cần phê duyệt HITL + backup bảng).
///
/// VỊ TRÍ TRONG MÔ HÌNH AN NINH — defense-in-depth, KHÔNG phải ranh giới duy nhất:
/// so khớp mẫu tĩnh luôn có thể bị né bằng dựng chuỗi động, vì vậy các quy tắc
/// dưới đây chặn cả cửa né phổ biến (__import__/importlib, eval/exec) và ranh
/// giới THẬT vẫn là sandbox: Jint không bật CLR, không có API mạng/file;
/// container Python chạy --network none, user nobody, cap-drop ALL,
/// no-new-privileges, rootfs chỉ-đọc. Tầng này đem lại: (1) từ chối SỚM với
/// thông điệp chính sách rõ ràng thay vì lỗi runtime khó hiểu, (2) log audit
/// mọi lần thử, (3) giữ nguyên chính sách nếu sau này cấu hình sandbox bị nới.
/// </summary>
public static class CodeExecutionGuard
{
    private const RegexOptions RuleOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>Một quy tắc chặn: mẫu regex + lý do (tiếng Việt, trả cho agent).</summary>
    public sealed record GuardRule(Regex Pattern, string Reason);

    // ── Quy tắc Python ──────────────────────────────────────────────────────
    private static readonly GuardRule[] PythonRules =
    [
        new(new Regex(@"\bsubprocess\b|\bpty\b|\bcommands\s*\.", RuleOptions),
            "gọi tiến trình hệ thống (subprocess/pty)"),
        new(new Regex(@"\bos\s*\.\s*(system|popen|exec\w*|spawn\w*|fork\w*|kill|killpg|abort)\b", RuleOptions),
            "thực thi lệnh hệ điều hành qua os.*"),
        new(new Regex(@"\bos\s*\.\s*(set(uid|gid|euid|egid|reuid|regid|groups)|chroot)\b", RuleOptions),
            "đổi định danh/leo thang quyền hạn tiến trình"),
        new(new Regex(@"\bos\s*\.\s*(environ|getenv|putenv|unsetenv)\b|\bdotenv\b", RuleOptions),
            "đọc/ghi biến môi trường (dò bí mật cấu hình)"),
        // Dạng "from os import X": os.path/os.getcwd hợp lệ vẫn qua, nhưng kéo thẳng
        // các tên nguy hiểm vào namespace thì chặn như gọi qua os.* ở trên.
        new(new Regex(@"from\s+os\s+import\s+[^\n]*\b(system|popen|exec\w*|spawn\w*|fork\w*|kill|environ|getenv|putenv|set(uid|gid|euid|egid))\b", RuleOptions),
            "kéo hàm hệ điều hành/biến môi trường vào namespace (from os import …)"),
        new(new Regex(@"\bctypes\b|\bcffi\b|\bctypes\.util\b", RuleOptions),
            "gọi thư viện native (ctypes/cffi)"),
        new(new Regex(@"\bsocket\b|\bssl\s*\.\s*wrap|\basyncio\s*\.\s*open_connection", RuleOptions),
            "mở kết nối mạng thô (socket) — sandbox không có mạng"),
        new(new Regex(@"\b(requests|urllib\d?|http\.client|httpx|aiohttp|ftplib|smtplib|telnetlib|paramiko|websocket|websockets)\b", RuleOptions),
            "thư viện HTTP/mạng — sandbox không có mạng; gọi API ngoài phải dùng tool call_api"),
        new(new Regex(@"\b(psycopg2?|psycopg|asyncpg|pg8000|pymongo|motor|redis|aioredis|sqlalchemy|pyodbc|cx_Oracle|oracledb|mysql(\.connector)?|mariadb|pymysql|clickhouse|cassandra|elasticsearch|qdrant_client)\b", RuleOptions),
            "kết nối trực tiếp cơ sở dữ liệu từ mã — đọc CSDL phải qua run_database_query (chỉ SELECT), ghi phải qua run_database_write (bắt buộc phê duyệt)"),
        new(new Regex(@"/proc/|/etc/passwd|/etc/shadow|/etc/sudoers|\.ssh/|id_rsa|/var/run/docker\.sock", RuleOptions),
            "dò tệp hệ thống/danh tính nhạy cảm"),
        new(new Regex(@"__import__|\bimportlib\b|\bbuiltins\s*\.", RuleOptions),
            "nạp module động (né kiểm tra tĩnh)"),
        // Lookbehind chặn eval(/exec( TRẦN nhưng vẫn cho pandas df.eval("...")/df.query(...)
        // — dấu chấm phía trước nghĩa là method của object, không phải builtin.
        new(new Regex(@"(?<![\w.])(eval|exec|compile)\s*\(", RuleOptions),
            "thực thi chuỗi mã động (eval/exec/compile)"),
    ];

    // ── Quy tắc JavaScript (Jint) ───────────────────────────────────────────
    private static readonly GuardRule[] JavaScriptRules =
    [
        new(new Regex(@"importNamespace|\bclr\s*\.|\bSystem\s*\.\s*\w", RuleOptions),
            "truy cập CLR/.NET từ script — engine không bật CLR interop"),
        new(new Regex(@"\brequire\s*\(|\bprocess\s*\.\s*\w|child_process|\bmodule\s*\.\s*exports|\b__dirname\b|\b__filename\b", RuleOptions),
            "API Node.js (require/process/fs…) — sandbox không phải Node"),
        new(new Regex(@"XMLHttpRequest|(?<![\w.])fetch\s*\(|WebSocket|EventSource|\bnavigator\s*\.", RuleOptions),
            "gọi mạng từ script — sandbox không có mạng; gọi API ngoài phải dùng tool call_api"),
        new(new Regex(@"\bFunction\s*\(\s*[""']", RuleOptions),
            "dựng hàm từ chuỗi (new Function) — né kiểm tra tĩnh"),
    ];

    /// <summary>
    /// Trả về THÔNG ĐIỆP TỪ CHỐI (agent-readable, kèm hướng dẫn đi đúng luồng)
    /// khi mã khớp một quy tắc chặn; null khi mã được phép vào sandbox.
    /// <paramref name="extraRules"/>: quy tắc bổ sung do vận hành khai báo
    /// (CodeInterpreter:Python:ExtraDenyPatterns), áp cho MỌI ngôn ngữ.
    /// <paramref name="matchedReason"/>: lý do thô cho log audit.
    /// </summary>
    public static string? Inspect(
        string code,
        GuardedCodeLanguage language,
        IReadOnlyList<GuardRule>? extraRules,
        out string? matchedReason)
    {
        matchedReason = null;
        if (string.IsNullOrEmpty(code)) return null;

        var rules = language == GuardedCodeLanguage.Python ? PythonRules : JavaScriptRules;
        foreach (var rule in rules)
        {
            if (rule.Pattern.IsMatch(code))
            {
                matchedReason = rule.Reason;
                return BuildRefusal(rule.Reason);
            }
        }

        if (extraRules is not null)
        {
            foreach (var rule in extraRules)
            {
                if (rule.Pattern.IsMatch(code))
                {
                    matchedReason = rule.Reason;
                    return BuildRefusal(rule.Reason);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Biên dịch danh sách mẫu cấu hình thành quy tắc. Mẫu regex hỏng được BỎ QUA
    /// nhưng báo qua <paramref name="onInvalidPattern"/> để vận hành thấy trong log
    /// — một mẫu sai chính tả không được phép đánh sập tool, nhưng cũng không được
    /// im lặng biến mất.
    /// </summary>
    public static IReadOnlyList<GuardRule> CompileExtraRules(
        IEnumerable<string>? patterns, Action<string, Exception>? onInvalidPattern = null)
    {
        var rules = new List<GuardRule>();
        foreach (var pattern in patterns ?? [])
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                rules.Add(new GuardRule(
                    new Regex(pattern, RuleOptions, TimeSpan.FromMilliseconds(200)),
                    $"mẫu bị cấm theo cấu hình vận hành ({pattern})"));
            }
            catch (ArgumentException ex)
            {
                onInvalidPattern?.Invoke(pattern, ex);
            }
        }
        return rules;
    }

    private static string BuildRefusal(string reason) =>
        $"[Sandbox Policy] Mã bị chặn theo quy tắc an toàn: {reason}. " +
        "Sandbox thực thi không có mạng và không truy cập được hệ thống/CSDL. " +
        "Đọc dữ liệu CSDL: dùng tool run_database_query (chỉ SELECT, transaction chỉ-đọc). " +
        "Ghi/xóa/cập nhật CSDL: dùng run_database_write — luôn cần người dùng phê duyệt và tự sao lưu bảng trước.";
}
