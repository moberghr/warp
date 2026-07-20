using Microsoft.AspNetCore.Mvc;
using Warp.Core;
using Warp.Core.Handlers;

namespace Warp.TestApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TestController : ControllerBase
{
    private readonly IPublisher _publisher;

    public TestController(IPublisher publisher)
    {
        _publisher = publisher;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(CustomerSignupRequest request)
    {
        await _publisher.Enqueue(request);
        return Ok();
    }

    [HttpPost("register-schedule")]
    public async Task<IActionResult> ScheduleRegister(ScheduleCustomerSignupRequest request)
    {
        await _publisher.Enqueue(request);
        return Ok();
    }

    [HttpPost("failed-job")]
    public async Task<IActionResult> FailedJob(FailedJobRequest request)
    {
        await _publisher.Enqueue(request);
        return Ok();
    }

    [HttpPost("recurring-job")]
    public async Task<IActionResult> RecurringJob(RecurringJobRequest request)
    {
        await _publisher.Enqueue(request);
        return Ok();
    }
}
