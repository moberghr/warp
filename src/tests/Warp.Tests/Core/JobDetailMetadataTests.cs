using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.Logging;
using Warp.Core.Models;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

// Metadata parsing on the read model and the JobContext default: plain objects, no database.
// Moved out of MetadataPublishPipelineTests, where each of these ran once per database for no reason.
[Trait("Category", "NoDb")]
public class JobDetailMetadataTests
{
    // --- Fix #5: Model metadata caching ---
    [TimedFact]
    public void UnifiedJobDetailModel_Metadata_CachedAcrossAccesses_1()
    {
        var model = new UnifiedJobDetailModel { MetadataJson = """{"key":"value"}""" };

        var first = model.Metadata;
        var second = model.Metadata;

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        ReferenceEquals(first, second).ShouldBeTrue("Metadata should be the same instance on repeated access");
    }

    [TimedFact]
    public void UnifiedJobDetailModel_Metadata_CachedAcrossAccesses_WithMultipleKeys()
    {
        var model = new UnifiedJobDetailModel { MetadataJson = """{"key1":"value1","key2":"value2"}""" };

        var first = model.Metadata;
        var second = model.Metadata;

        first.ShouldNotBeNull();
        first.Count.ShouldBe(2);
        second.ShouldNotBeNull();
        ReferenceEquals(first, second).ShouldBeTrue("Metadata should be the same instance on repeated access");
    }

    [TimedFact]
    public void UnifiedJobDetailModel_NullMetadataJson_ReturnsNull()
    {
        var model = new UnifiedJobDetailModel { MetadataJson = null };
        model.Metadata.ShouldBeNull();
    }

    // --- Edge case: JobContext with null metadata → Metadata is empty dict, not null ---
    [TimedFact]
    public void JobContext_DefaultMetadata_IsEmptyDictionaryNotNull()
    {
        var ctx = new JobContext();
        ctx.Metadata.ShouldNotBeNull();
        ctx.Metadata.Count.ShouldBe(0);
    }
}
