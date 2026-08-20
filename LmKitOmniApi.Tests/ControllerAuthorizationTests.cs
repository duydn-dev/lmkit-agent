using System.Reflection;
using LmKitOmniApi.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace LmKitOmniApi.Tests;

public class ControllerAuthorizationTests
{
    [Theory]
    [InlineData(typeof(AgentsController))]
    [InlineData(typeof(ChatController))]
    [InlineData(typeof(DocumentController))]
    [InlineData(typeof(KnowledgeBaseController))]
    [InlineData(typeof(MemoryController))]
    [InlineData(typeof(SpeechController))]
    [InlineData(typeof(TaskApprovalController))]
    [InlineData(typeof(TextAnalysisController))]
    [InlineData(typeof(VisionController))]
    public void AiController_RequiresAuthorization(Type controllerType)
    {
        var controllerAuthorized = controllerType.GetCustomAttribute<AuthorizeAttribute>() is not null;
        var allActionsAuthorized = controllerType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<Microsoft.AspNetCore.Mvc.NonActionAttribute>() is null)
            .All(method => method.GetCustomAttribute<AuthorizeAttribute>() is not null);

        Assert.True(controllerAuthorized || allActionsAuthorized, $"{controllerType.Name} is not protected by [Authorize].");
    }
}
