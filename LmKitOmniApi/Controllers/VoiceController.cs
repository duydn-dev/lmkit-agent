using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // Yêu cầu đăng nhập mới được lấy token gọi thoại
public class VoiceController : ControllerBase
{
    // [HttpGet("voice-token")]
    // public IActionResult GetVoiceToken([FromQuery] string roomName = "main-room")
    // {
    //     var userName = User.Identity?.Name ?? "Anonymous";
    //
    //     // Yêu cầu thư viện LiveKit.Server. Cần cài đặt sau khi có bản chính thức hỗ trợ .NET 10.
    //     // Tham khảo: dotnet add package LiveKit.Server
    //     
    //     /*
    //     var token = new AccessToken("your_api_key", "your_api_secret")
    //         .WithIdentity(userName)
    //         .WithName(userName)
    //         .WithGrants(new VideoGrant { 
    //             RoomJoin = true, 
    //             Room = roomName, 
    //             CanPublish = true, 
    //             CanSubscribe = true 
    //         });
    //
    //     return Ok(new { Token = token.ToJwt() });
    //     */
    //
    //     return Ok(new { Token = "dummy_token_for_now" });
    // }
}
