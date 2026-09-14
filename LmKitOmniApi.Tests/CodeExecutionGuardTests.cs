using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Tầng quy tắc tĩnh chặn mã trước sandbox: leo thang quyền hạn (tiến trình/
/// định danh/CLR), dò bí mật (env/tệp hệ thống), driver CSDL + thư viện mạng,
/// và các cửa né phổ biến (__import__, eval/exec, new Function). Cả hai executor
/// phải TỪ CHỐI TRƯỚC KHI THỰC THI — Python không được tạo container.
/// </summary>
public sealed class CodeExecutionGuardTests
{
    private static string? InspectPython(string code) =>
        CodeExecutionGuard.Inspect(code, GuardedCodeLanguage.Python, null, out _);

    private static string? InspectJs(string code) =>
        CodeExecutionGuard.Inspect(code, GuardedCodeLanguage.JavaScript, null, out _);

    // ── Python: leo thang quyền hạn / tiến trình ──────────────────────────

    [Theory]
    [InlineData("import subprocess\nsubprocess.run(['whoami'])")]
    [InlineData("import os\nos.system('rm -rf /')")]
    [InlineData("import os\nos.popen('id').read()")]
    [InlineData("import os\nos.execv('/bin/sh', ['sh'])")]
    [InlineData("import os\nos.setuid(0)")]
    [InlineData("from os import system\nsystem('id')")]
    [InlineData("import ctypes\nctypes.CDLL('libc.so.6')")]
    [InlineData("import pty\npty.spawn('/bin/bash')")]
    public void Python_PrivilegeEscalationVectors_AreBlocked(string code)
    {
        var refusal = InspectPython(code);
        Assert.NotNull(refusal);
        Assert.StartsWith("[Sandbox Policy]", refusal);
    }

    // ── Python: dò bí mật ─────────────────────────────────────────────────

    [Theory]
    [InlineData("import os\nprint(os.environ)")]
    [InlineData("import os\nprint(os.getenv('ConnectionStrings__PostgreSql'))")]
    [InlineData("from os import environ\nprint(environ)")]
    [InlineData("print(open('/etc/passwd').read())")]
    [InlineData("print(open('/proc/self/environ').read())")]
    [InlineData("data = open('/root/.ssh/id_rsa').read()")]
    public void Python_SecretProbingVectors_AreBlocked(string code)
        => Assert.NotNull(InspectPython(code));

    // ── Python: driver CSDL + mạng — thao tác dữ liệu phải qua tool DB ────

    [Theory]
    [InlineData("import psycopg2\nconn = psycopg2.connect('host=postgres')\nconn.cursor().execute('DELETE FROM users')")]
    [InlineData("import pymongo\npymongo.MongoClient('mongodb://db')" )]
    [InlineData("import redis\nredis.Redis(host='redis')")]
    [InlineData("import sqlalchemy\nsqlalchemy.create_engine('postgresql://')")]
    [InlineData("from qdrant_client import QdrantClient")]
    [InlineData("import socket\nsocket.create_connection(('10.0.0.5', 5432))")]
    [InlineData("import requests\nrequests.post('http://internal/api')")]
    [InlineData("import urllib.request\nurllib.request.urlopen('http://x')")]
    public void Python_DatabaseAndNetworkClients_AreBlocked(string code)
    {
        var refusal = InspectPython(code);
        Assert.NotNull(refusal);
        // Thông điệp phải chỉ đường đi đúng: tool DB có phê duyệt.
        Assert.Contains("run_database_query", refusal);
        Assert.Contains("run_database_write", refusal);
    }

    // ── Python: cửa né kiểm tra tĩnh ──────────────────────────────────────

    [Theory]
    [InlineData("__import__('os').system('id')")]
    [InlineData("import importlib\nimportlib.import_module('subprocess')")]
    [InlineData("eval(\"__import__('os')\")")]
    [InlineData("exec('import socket')")]
    public void Python_DynamicImportAndEval_AreBlocked(string code)
        => Assert.NotNull(InspectPython(code));

    // ── Python: mã phân tích dữ liệu hợp lệ phải ĐI QUA ───────────────────

    [Theory]
    [InlineData("import pandas as pd\ndf = pd.read_csv('data.csv')\nprint(df.describe())")]
    [InlineData("import os.path\nprint(os.path.join('a', 'b'))")]
    [InlineData("import math, json\nprint(json.dumps({'pi': math.pi}))")]
    [InlineData("df = df[df.total > 0]\nprint(df.eval('total * 2'))")] // pandas .eval là method, không phải builtin
    [InlineData("import matplotlib\nimport matplotlib.pyplot as plt\nplt.savefig('bieu-do.png')")]
    [InlineData("with open('ket-qua.txt', 'w') as f:\n    f.write('xong')")]
    public void Python_LegitimateAnalyticsCode_IsAllowed(string code)
        => Assert.Null(InspectPython(code));

    // ── JavaScript ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("var fs = importNamespace('System.IO');")]
    [InlineData("System.Diagnostics.Process.Start('cmd')")]
    [InlineData("const cp = require('child_process');")]
    [InlineData("console.log(process.env.JWT_SECRET)")]
    [InlineData("fetch('http://10.0.0.1/admin')")]
    [InlineData("new XMLHttpRequest()")]
    [InlineData("new WebSocket('ws://internal')")]
    [InlineData("var f = new Function('return this')();")]
    public void JavaScript_EscalationAndNetworkVectors_AreBlocked(string code)
    {
        var refusal = InspectJs(code);
        Assert.NotNull(refusal);
        Assert.StartsWith("[Sandbox Policy]", refusal);
    }

    [Theory]
    [InlineData("const total = [1,2,3].reduce((a,b) => a+b, 0); total")]
    [InlineData("JSON.stringify({ systemOfRecord: true })")] // "systemOf..." không phải "System."
    [InlineData("console.log('đã xử lý', 42)")]
    [InlineData("const data = 'requirements: fetched later'; data.length")] // từ khóa trong CHUỖI vẫn khớp? không — 'fetch' cần '(' theo sau
    public void JavaScript_LegitimateCode_IsAllowed(string code)
        => Assert.Null(InspectJs(code));

    // ── Mẫu bổ sung từ cấu hình ───────────────────────────────────────────

    [Fact]
    public void ExtraPatterns_FromConfiguration_AreEnforced_AndBrokenPatternsAreReported()
    {
        var invalid = new List<string>();
        var rules = CodeExecutionGuard.CompileExtraRules(
            new[] { @"\bbitcoin\b", "((broken" },
            (pattern, _) => invalid.Add(pattern));

        Assert.Single(invalid);
        Assert.Equal("((broken", invalid[0]);

        var refusal = CodeExecutionGuard.Inspect(
            "mine_bitcoin()", GuardedCodeLanguage.Python, rules, out var reason);
        Assert.Null(refusal); // "mine_bitcoin" không khớp \bbitcoin\b? — khớp: '_' là ký tự từ nên KHÔNG có boundary

        refusal = CodeExecutionGuard.Inspect(
            "start bitcoin miner", GuardedCodeLanguage.Python, rules, out reason);
        Assert.NotNull(refusal);
        Assert.Contains("cấu hình vận hành", reason);
    }

    // ── Wiring: executor phải từ chối TRƯỚC KHI thực thi ──────────────────

    [Fact]
    public async Task JintEngine_RefusesBlockedCode_WithThePolicyMessage()
    {
        var engine = new ExecutionSandboxEngine(
            Options.Create(new CodeInterpreterOptions()),
            NullLogger<ExecutionSandboxEngine>.Instance);

        var result = await engine.ExecuteCodeSafelyAsync(
            "importNamespace('System.IO').File.ReadAllText('appsettings.json')", "javascript");

        Assert.StartsWith("[Sandbox Policy]", result);
    }

    [Fact]
    public async Task JintEngine_StillRunsLegitimateCode()
    {
        var engine = new ExecutionSandboxEngine(
            Options.Create(new CodeInterpreterOptions()),
            NullLogger<ExecutionSandboxEngine>.Instance);

        var result = await engine.ExecuteCodeSafelyAsync("1 + 2", "javascript");
        Assert.Equal("3", result);
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public int Invocations;
        public Task<ProcessRunResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string? stdin, TimeSpan timeout, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty, false));
        }
    }

    [Fact]
    public async Task PythonExecutor_RefusesBlockedCode_BeforeAnyContainerIsLaunched()
    {
        var runner = new RecordingRunner();
        var executor = new PythonContainerExecutor(
            Options.Create(new CodeInterpreterOptions { Enabled = true, Image = "python:3.12-alpine" }),
            runner,
            new UserResourceAccessService(new ToolSandboxService(NullLogger<ToolSandboxService>.Instance)),
            NullLogger<PythonContainerExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            "import subprocess\nsubprocess.run(['cat', '/etc/passwd'])", Guid.NewGuid(), Guid.NewGuid());

        Assert.StartsWith("[Sandbox Policy]", result.Output);
        Assert.Equal(0, runner.Invocations); // container KHÔNG ĐƯỢC tạo
    }
}
