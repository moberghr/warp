using Shouldly;
using Warp.Core.Data.Converters;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Direct coverage for <c>RetryScheduleConverter.Deserialize</c> (W-4): the dispatcher always writes at
/// least <c>"[]"</c>, so a blank / JSON-null column is corrupt and must fail loud rather than silently
/// disable retries by materializing an empty schedule. <c>"[]"</c> is the legitimate single-attempt shape
/// and roundtrips to an empty list.
/// </summary>
[Trait("Category", "NoDb")]
public class RetryScheduleConverterDeserializeTests
{
    [TimedFact]
    public void Deserialize_EmptyArrayLiteral_ReturnsEmptySchedule()
    {
        RetryScheduleConverter.Deserialize("[]").ShouldBeEmpty();
    }

    [TimedFact]
    public void Deserialize_WhitespaceColumn_Throws()
    {
        Should.Throw<InvalidOperationException>(() => RetryScheduleConverter.Deserialize("   "));
    }

    [TimedFact]
    public void Deserialize_EmptyColumn_Throws()
    {
        Should.Throw<InvalidOperationException>(() => RetryScheduleConverter.Deserialize(string.Empty));
    }

    [TimedFact]
    public void Deserialize_JsonNullColumn_Throws()
    {
        Should.Throw<InvalidOperationException>(() => RetryScheduleConverter.Deserialize("null"));
    }

    [TimedFact]
    public void Deserialize_PopulatedArray_ReturnsSeconds()
    {
        var schedule = RetryScheduleConverter.Deserialize("[60,600]");

        schedule.ShouldBe([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)]);
    }
}
