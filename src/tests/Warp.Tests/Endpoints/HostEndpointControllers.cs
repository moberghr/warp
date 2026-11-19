using Microsoft.AspNetCore.Mvc;
using Warp.Http.Observability;

namespace Warp.Tests.Endpoints;

// Real MVC controllers for the host-endpoint observability tests. Only HostEndpointObservabilityTestsBase
// registers them (it restricts the application part to exactly these types).

/// <summary>Opted in only through <c>MapControllers().WithWarpObservability()</c>.</summary>
[ApiController]
[Route("api/host-orders")]
public sealed class HostOrdersController : ControllerBase
{
    [HttpGet("{id:int}")]
    public IActionResult Get(int id) => Ok(new { id });

    [HttpGet("boom")]
    public IActionResult Boom() => throw new InvalidOperationException($"host-order-boom at {Request.Path}");
}

/// <summary>Never opted in.</summary>
[ApiController]
[Route("api/host-plain")]
public sealed class HostPlainController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

/// <summary>Opted in for every action by the controller-level attribute.</summary>
[ApiController]
[WarpObserved]
[Route("api/host-attributed")]
public sealed class HostAttributedController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

/// <summary>Only <c>On</c> is opted in, by an action-level attribute.</summary>
[ApiController]
[Route("api/host-action")]
public sealed class HostActionController : ControllerBase
{
    [WarpObserved]
    [HttpGet("on")]
    public IActionResult On() => Ok();

    [HttpGet("off")]
    public IActionResult Off() => Ok();
}
