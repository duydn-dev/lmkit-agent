using System.Security.Claims;
using LmKitOmniApi.Controllers;
using LmKitOmniApi.Infrastructure.AI.ComputerUse;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Controller-level guards for <see cref="ComputerUseController"/>, exercised by driving
/// the action directly with a fabricated <see cref="HttpContext"/> (no server, no DI): the
/// stream endpoint returns 501 when the tool is OFF (the default), 400 on a missing task,
/// and 403 for a non Admin/User role — all BEFORE any streaming or mediator call.
/// </summary>
public class ComputerUseControllerTests
{
    private sealed class StubAgent : IComputerUseAgent
    {
        public bool IsEnabled { get; init; }
        public bool Ran { get; private set; }

        public async IAsyncEnumerable<string> RunAsync(
            ComputerUseRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Ran = true;
            yield return "[COMPUTER_USE:ran]";
            await Task.CompletedTask;
        }
    }

    private static ComputerUseController CreateController(IComputerUseAgent agent, string role, out DefaultHttpContext http)
    {
        // IMediator is only used by the approve/reject endpoints, never by the guarded
        // stream path under test — so it is safe to omit here.
        var controller = new ComputerUseController(agent, mediator: null!, NullLogger<ComputerUseController>.Instance);

        http = new DefaultHttpContext();
        var claims = new List<Claim>
        {
            new("TenantId", Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        };
        if (!string.IsNullOrEmpty(role)) claims.Add(new Claim("Role", role));
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    [Fact]
    public async Task Run_WhenDisabled_Returns501_AndNeverRunsTheLoop()
    {
        var agent = new StubAgent { IsEnabled = false };
        var controller = CreateController(agent, "User", out var http);

        await controller.Run(new ComputerUseController.StartComputerUseRequest("book a table", null), default);

        Assert.Equal(StatusCodes.Status501NotImplemented, http.Response.StatusCode);
        Assert.False(agent.Ran);
    }

    [Fact]
    public async Task Run_WhenEnabled_StreamsAndSetsSseHeaders()
    {
        var agent = new StubAgent { IsEnabled = true };
        var controller = CreateController(agent, "User", out var http);
        http.Response.Body = new MemoryStream();

        await controller.Run(new ComputerUseController.StartComputerUseRequest("book a table", null), default);

        Assert.True(agent.Ran);
        Assert.Equal("text/event-stream", http.Response.Headers["Content-Type"].ToString());
    }

    [Fact]
    public async Task Run_WithEmptyTask_Returns400_BeforeEnableCheck()
    {
        var agent = new StubAgent { IsEnabled = true };
        var controller = CreateController(agent, "User", out var http);

        await controller.Run(new ComputerUseController.StartComputerUseRequest("   ", null), default);

        Assert.Equal(StatusCodes.Status400BadRequest, http.Response.StatusCode);
        Assert.False(agent.Ran);
    }

    [Fact]
    public async Task Run_ForGuestRole_Returns403()
    {
        var agent = new StubAgent { IsEnabled = true };
        var controller = CreateController(agent, "Guest", out var http);

        await controller.Run(new ComputerUseController.StartComputerUseRequest("book a table", null), default);

        Assert.Equal(StatusCodes.Status403Forbidden, http.Response.StatusCode);
        Assert.False(agent.Ran);
    }
}
