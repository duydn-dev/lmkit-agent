using System.Reflection;
using System.Security.Claims;
using System.Text;
using LmKitOmniApi.Application.LoraAdapters;
using LmKitOmniApi.Application.LoraAdapters.Commands;
using LmKitOmniApi.Application.LoraAdapters.Queries;
using LmKitOmniApi.Controllers;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Controller-contract tests for <see cref="LoraAdaptersController"/> — identity handling,
/// the result-status → HTTP-status mapping (incl. 501 when the feature is off), and the
/// Admin-only surface on registration mutations. Uses a stub <see cref="IMediator"/> so the
/// mapping is exercised deterministically without DI or a host.
/// </summary>
public sealed class LoraAdaptersControllerTests
{
    private static LoraAdaptersController Build(IMediator mediator, bool withIdentity = true, string role = "Admin")
    {
        var claims = new List<Claim>();
        if (withIdentity)
        {
            claims.Add(new Claim("TenantId", Guid.NewGuid().ToString()));
            claims.Add(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, withIdentity ? "test" : null));
        return new LoraAdaptersController(mediator)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } }
        };
    }

    private static IFormFile FakeFile(int bytes = 32) =>
        new FormFile(new MemoryStream(Encoding.ASCII.GetBytes(new string('a', bytes))), 0, bytes, "file", "adapter.gguf");

    private static int? StatusOf(IActionResult result) => (result as ObjectResult)?.StatusCode
        ?? (result as StatusCodeResult)?.StatusCode;

    // ── Identity ──

    [Fact]
    public async Task List_WithoutIdentity_ReturnsUnauthorized()
    {
        var controller = Build(new StubMediator(new List<LoraAdapterDto>()), withIdentity: false);
        Assert.IsType<UnauthorizedResult>(await controller.List(CancellationToken.None));
    }

    // ── GET mapping ──

    [Fact]
    public async Task List_ReturnsOk_WithAdapters()
    {
        var controller = Build(new StubMediator((IReadOnlyList<LoraAdapterDto>)new List<LoraAdapterDto> { new() { Name = "a" } }));
        var result = await controller.List(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IReadOnlyList<LoraAdapterDto>>(ok.Value);
        Assert.Single(list);
    }

    [Fact]
    public async Task Get_ReturnsNotFound_WhenNull()
    {
        var controller = Build(new StubMediator((LoraAdapterDto?)null));
        Assert.IsType<NotFoundResult>(await controller.Get(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Get_ReturnsOk_WhenPresent()
    {
        var controller = Build(new StubMediator((LoraAdapterDto?)new LoraAdapterDto { Name = "a" }));
        Assert.IsType<OkObjectResult>(await controller.Get(Guid.NewGuid(), CancellationToken.None));
    }

    // ── Upload (register) mapping ──

    [Fact]
    public async Task Upload_WithoutFile_ReturnsBadRequest()
    {
        var controller = Build(new StubMediator(LoraAdapterMutationResult.Success()));
        var result = await controller.Upload("name", null, null, null, file: null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Upload_FeatureDisabled_Returns501()
    {
        var controller = Build(new StubMediator(LoraAdapterMutationResult.FeatureDisabled()));
        var result = await controller.Upload("name", null, null, null, FakeFile(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status501NotImplemented, StatusOf(result));
    }

    [Fact]
    public async Task Upload_ValidationFailed_ReturnsBadRequest()
    {
        var controller = Build(new StubMediator(LoraAdapterMutationResult.ValidationFailed("bad")));
        Assert.IsType<BadRequestObjectResult>(await controller.Upload("n", null, null, null, FakeFile(), CancellationToken.None));
    }

    [Fact]
    public async Task Upload_Success_ReturnsCreated()
    {
        var dto = new LoraAdapterDto { Id = Guid.NewGuid(), Name = "a" };
        var controller = Build(new StubMediator(LoraAdapterMutationResult.Success(dto)));
        Assert.IsType<CreatedAtActionResult>(await controller.Upload("a", null, 1.0f, null, FakeFile(), CancellationToken.None));
    }

    // ── Update mapping ──

    [Theory]
    [InlineData(LoraMutationStatus.FeatureDisabled, StatusCodes.Status501NotImplemented)]
    public async Task Update_FeatureDisabled_Returns501(LoraMutationStatus status, int expected)
    {
        var controller = Build(new StubMediator(new LoraAdapterMutationResult { Status = status }));
        var result = await controller.Update(Guid.NewGuid(), new UpdateLoraAdapterRequest { Name = "x" }, CancellationToken.None);
        Assert.Equal(expected, StatusOf(result));
    }

    [Fact]
    public async Task Update_NotFound_Returns404()
    {
        var controller = Build(new StubMediator(LoraAdapterMutationResult.NotFound()));
        Assert.IsType<NotFoundResult>(await controller.Update(Guid.NewGuid(), new UpdateLoraAdapterRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task Update_Success_ReturnsOk()
    {
        var controller = Build(new StubMediator(LoraAdapterMutationResult.Success(new LoraAdapterDto { Name = "a" })));
        Assert.IsType<OkObjectResult>(await controller.Update(Guid.NewGuid(), new UpdateLoraAdapterRequest(), CancellationToken.None));
    }

    // ── Delete mapping ──

    [Fact]
    public async Task Delete_FeatureDisabled_Returns501()
    {
        var controller = Build(new StubMediator(LoraAdapterMutationResult.FeatureDisabled()));
        Assert.Equal(StatusCodes.Status501NotImplemented, StatusOf(await controller.Delete(Guid.NewGuid(), CancellationToken.None)));
    }

    [Fact]
    public async Task Delete_NotFound_Returns404()
    {
        var controller = Build(new StubMediator(LoraAdapterMutationResult.NotFound()));
        Assert.IsType<NotFoundResult>(await controller.Delete(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Delete_Success_ReturnsNoContent()
    {
        var controller = Build(new StubMediator(LoraAdapterMutationResult.Success()));
        Assert.IsType<NoContentResult>(await controller.Delete(Guid.NewGuid(), CancellationToken.None));
    }

    // ── Assign mapping ──

    [Fact]
    public async Task Assign_EmptyAgentId_ReturnsBadRequest()
    {
        var controller = Build(new StubMediator(LoraAssignResult.Success()));
        Assert.IsType<BadRequestObjectResult>(await controller.Assign(Guid.NewGuid(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task Assign_AgentNotFound_Returns404()
    {
        var controller = Build(new StubMediator(LoraAssignResult.AgentNotFound()));
        Assert.IsType<NotFoundResult>(await controller.Assign(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Assign_AdapterNotFound_ReturnsBadRequest()
    {
        var controller = Build(new StubMediator(LoraAssignResult.AdapterNotFound()));
        Assert.IsType<BadRequestObjectResult>(await controller.Assign(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Assign_Success_ReturnsNoContent()
    {
        var controller = Build(new StubMediator(LoraAssignResult.Success()));
        Assert.IsType<NoContentResult>(await controller.Assign(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Assign_FeatureDisabled_Returns501()
    {
        var controller = Build(new StubMediator(LoraAssignResult.FeatureDisabled()));
        Assert.Equal(StatusCodes.Status501NotImplemented, StatusOf(await controller.Assign(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)));
    }

    // ── Admin-only surface (registration mutations) ──

    [Theory]
    [InlineData(nameof(LoraAdaptersController.Upload))]
    [InlineData(nameof(LoraAdaptersController.Update))]
    [InlineData(nameof(LoraAdaptersController.Delete))]
    public void RegistrationMutations_AreAdminOnly(string methodName)
    {
        var method = typeof(LoraAdaptersController).GetMethod(methodName)!;
        var authorize = method.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal("Admin", authorize!.Roles);
    }

    [Fact]
    public void Controller_RequiresAuthorization_AtClassLevel()
    {
        Assert.NotNull(typeof(LoraAdaptersController).GetCustomAttribute<AuthorizeAttribute>());
    }

    /// <summary>Minimal IMediator (MediatR 14) whose Send returns a canned response; the rest throw.</summary>
    private sealed class StubMediator : IMediator
    {
        private readonly object? _response;
        public StubMediator(object? response) => _response = response;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => Task.FromResult((TResponse)_response!);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
            => throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
            => throw new NotSupportedException();
    }
}
