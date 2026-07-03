using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;

namespace LmKitOmniApi.Infrastructure.Security
{
    public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            var httpContext = context.GetHttpContext();

            // In production, we must require an authenticated user, typically with "Admin" role.
            // Example using Identity/Claims:
            // return httpContext.User.Identity?.IsAuthenticated == true && httpContext.User.IsInRole("Admin");

            // For this setup, we will check if the user is authenticated as a temporary secure measure.
            // Ensure you have cookie/JWT configured so the dashboard route can read the identity.
            
            // Allow local access without auth for development convenience
            if (httpContext.Request.Host.Host == "localhost" || httpContext.Request.Host.Host == "127.0.0.1")
            {
                return true;
            }

            return httpContext.User.Identity?.IsAuthenticated == true && httpContext.User.IsInRole("Admin");
        }
    }
}
