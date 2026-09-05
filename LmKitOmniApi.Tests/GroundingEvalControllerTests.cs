using System.Reflection;
using LmKitOmniApi.Controllers;
using LmKitOmniApi.Infrastructure.AI.ComputerUse;
using LmKitOmniApi.Infrastructure.AI.ComputerUse.Eval;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Controller-level guards for <see cref="GroundingEvalController"/>, driven directly with a
/// stub evaluator (no server, no DI): 501 when the harness is OFF (the default), the default
/// fixture set when the body is omitted, pass-through of a supplied body, and 400 when the
/// per-request cap is exceeded. The Admin-only gate is declarative
/// (<c>[Authorize(Roles="Admin")]</c>) and asserted by reflection here + centrally by
/// <see cref="ControllerAuthorizationTests"/>.
/// </summary>
public class GroundingEvalControllerTests
{
    private sealed class StubEvaluator : IGroundingEvaluator
    {
        public bool IsEnabled { get; init; }
        public IReadOnlyList<GroundingEvalCase>? ReceivedCases { get; private set; }
        public GroundingEvalReport Report { get; init; } = new() { Total = 0 };

        public Task<GroundingEvalReport> EvaluateAsync(IReadOnlyList<GroundingEvalCase> cases, CancellationToken ct = default)
        {
            ReceivedCases = cases;
            return Task.FromResult(Report);
        }
    }

    private static GroundingEvalController Controller(IGroundingEvaluator evaluator)
    {
        var controller = new GroundingEvalController(evaluator, NullLogger<GroundingEvalController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return controller;
    }

    [Fact]
    public async Task Run_WhenDisabled_Returns501_AndNeverEvaluates()
    {
        var evaluator = new StubEvaluator { IsEnabled = false };
        var result = await Controller(evaluator).Run(request: null, default);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status501NotImplemented, status.StatusCode);
        Assert.Null(evaluator.ReceivedCases); // never evaluated
    }

    [Fact]
    public async Task Run_WhenEnabled_NullBody_UsesDefaultFixtureSet()
    {
        var evaluator = new StubEvaluator { IsEnabled = true };
        var result = await Controller(evaluator).Run(request: null, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(evaluator.ReceivedCases);
        // The built-in default fixture set is non-empty and matches GroundingEvalFixtures.Default().
        Assert.Equal(GroundingEvalFixtures.Default().Count, evaluator.ReceivedCases!.Count);
        Assert.NotEmpty(evaluator.ReceivedCases);
    }

    [Fact]
    public async Task Run_WhenEnabled_WithSuppliedCases_PassesThemThrough()
    {
        var evaluator = new StubEvaluator { IsEnabled = true };
        var supplied = new List<GroundingEvalCase>
        {
            new("goal", new ComputerUseObservation
            {
                Url = "https://example.com/",
                Title = "t",
                Elements = new[] { new InteractiveElement(1, "button", "OK", null) },
            }, ExpectedRef: 1),
        };

        var result = await Controller(evaluator).Run(new GroundingEvalController.RunRequest(supplied), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Same(supplied, evaluator.ReceivedCases);
    }

    [Fact]
    public async Task Run_WhenEnabled_TooManyCases_Returns400()
    {
        var evaluator = new StubEvaluator { IsEnabled = true };
        var obs = new ComputerUseObservation
        {
            Url = "https://example.com/",
            Title = "t",
            Elements = new[] { new InteractiveElement(1, "button", "OK", null) },
        };
        var many = Enumerable.Range(0, 201).Select(_ => new GroundingEvalCase("g", obs, 1)).ToList();

        var result = await Controller(evaluator).Run(new GroundingEvalController.RunRequest(many), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(evaluator.ReceivedCases); // rejected before evaluation
    }

    [Fact]
    public async Task Run_WhenEnabled_CaseWithEmptyGoal_Returns400()
    {
        var evaluator = new StubEvaluator { IsEnabled = true };
        var supplied = new List<GroundingEvalCase>
        {
            new("   ", new ComputerUseObservation { Url = "https://example.com/", Title = "t" }, ExpectedRef: 1),
        };

        var result = await Controller(evaluator).Run(new GroundingEvalController.RunRequest(supplied), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(evaluator.ReceivedCases);
    }

    [Fact]
    public void Controller_IsAdminOnly_AndRoutedCorrectly()
    {
        var authorize = typeof(GroundingEvalController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal("Admin", authorize!.Roles);

        var route = typeof(GroundingEvalController).GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(route);
        Assert.Equal("api/computer-use/grounding-eval", route!.Template);
    }
}
