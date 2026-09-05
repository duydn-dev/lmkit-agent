using System.Reflection;
using System.Security.Claims;
using LmKitOmniApi.Controllers;
using LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Controller-contract tests for <see cref="GroundingTrainingController"/> — the off-by-default
/// 501 gate on BOTH endpoints, the outcome → HTTP-status mapping, and the Admin-only surface on
/// <c>run</c>. Driven directly with a fabricated identity and a stub service (no host, no DI).
/// </summary>
public sealed class GroundingTrainingControllerTests
{
    private sealed class StubService : IGroundingTrainingService
    {
        public bool Enabled { get; init; }
        public int Count { get; init; }
        public GroundingTrainingRunResult RunResult { get; init; } =
            GroundingTrainingRunResult.Trained(Guid.NewGuid(), "/x/adapter.gguf", 60);

        public Task<int> CountSamplesAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult(Count);
        public Task<GroundingTrainingRunResult> TrainAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult(RunResult);
    }

    private static GroundingTrainingController Build(IGroundingTrainingService service, bool withIdentity = true)
    {
        var claims = new List<Claim>();
        if (withIdentity)
        {
            claims.Add(new Claim("TenantId", Guid.NewGuid().ToString()));
            claims.Add(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, withIdentity ? "test" : null));
        return new GroundingTrainingController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } }
        };
    }

    private static int? StatusOf(IActionResult result) =>
        (result as ObjectResult)?.StatusCode ?? (result as StatusCodeResult)?.StatusCode;

    // ── Off by default → 501 on both endpoints ──

    [Fact]
    public async Task Stats_WhenDisabled_Returns501()
    {
        var controller = Build(new StubService { Enabled = false });
        Assert.Equal(StatusCodes.Status501NotImplemented, StatusOf(await controller.Stats(CancellationToken.None)));
    }

    [Fact]
    public async Task Run_WhenDisabled_Returns501()
    {
        var controller = Build(new StubService { Enabled = false });
        Assert.Equal(StatusCodes.Status501NotImplemented, StatusOf(await controller.Run(CancellationToken.None)));
    }

    // ── Enabled mappings ──

    [Fact]
    public async Task Stats_WhenEnabled_ReturnsOkWithCount()
    {
        var controller = Build(new StubService { Enabled = true, Count = 42 });
        var ok = Assert.IsType<OkObjectResult>(await controller.Stats(CancellationToken.None));
        Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);
    }

    [Fact]
    public async Task Run_InsufficientSamples_Returns409()
    {
        var controller = Build(new StubService
        {
            Enabled = true,
            RunResult = GroundingTrainingRunResult.InsufficientSamples(3, 50)
        });
        Assert.IsType<ConflictObjectResult>(await controller.Run(CancellationToken.None));
    }

    [Fact]
    public async Task Run_Trained_ReturnsOk()
    {
        var controller = Build(new StubService
        {
            Enabled = true,
            RunResult = GroundingTrainingRunResult.Trained(Guid.NewGuid(), "/x/adapter.gguf", 60)
        });
        Assert.IsType<OkObjectResult>(await controller.Run(CancellationToken.None));
    }

    [Fact]
    public async Task Run_TrainingFailed_Returns500()
    {
        var controller = Build(new StubService
        {
            Enabled = true,
            RunResult = GroundingTrainingRunResult.Failed("boom")
        });
        Assert.Equal(StatusCodes.Status500InternalServerError, StatusOf(await controller.Run(CancellationToken.None)));
    }

    [Fact]
    public async Task Stats_WithoutIdentity_ReturnsUnauthorized()
    {
        var controller = Build(new StubService { Enabled = true }, withIdentity: false);
        Assert.IsType<UnauthorizedResult>(await controller.Stats(CancellationToken.None));
    }

    // ── Admin-only surface + class-level auth ──

    [Fact]
    public void Run_IsAdminOnly()
    {
        var method = typeof(GroundingTrainingController).GetMethod(nameof(GroundingTrainingController.Run))!;
        var authorize = method.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal("Admin", authorize!.Roles);
    }

    [Fact]
    public void Controller_RequiresAuthorization_AtClassLevel()
    {
        Assert.NotNull(typeof(GroundingTrainingController).GetCustomAttribute<AuthorizeAttribute>());
    }
}
