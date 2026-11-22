using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.Helper;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Features.Concurrency;

// [Mutex]'s constructor arguments are validated when the attribute is built: no worker and no database involved.
// Moved out of MutexTests, where each of these ran once per database for no reason.
[Trait("Category", "NoDb")]
public class MutexAttributeValidationTests
{
    [TimedFact]
    public Task MutexAttribute_EmptyKey_ThrowsAtConstruction()
    {
        Should.Throw<ArgumentException>(() => new MutexAttribute(string.Empty));
        Should.Throw<ArgumentNullException>(() => new MutexAttribute(null!));

        return Task.CompletedTask;
    }
}
