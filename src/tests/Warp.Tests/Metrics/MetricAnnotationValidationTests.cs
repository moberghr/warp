using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Models;
using Warp.Core.Slo;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb coverage for metric annotations (#333): the shape validation and normalization the upsert route runs before the
/// registry / objective checks, the author normalization, the filter overlap rule that attaches a note to a board tile,
/// and the acknowledged-noise verdict.
/// </summary>
[Trait("Category", "NoDb")]
public class MetricAnnotationValidationTests
{
    private static readonly DateTime Start = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    [TimedFact]
    public void TryValidate_InstrumentNote_TrimsAndClearsTheSloFields()
    {
        var annotation = InstrumentNote();
        annotation.Note = "  duplicate provider notification race  ";
        annotation.TagFilter = "  status=5*  ";
        annotation.SloDefinitionId = 4;

        MetricAnnotationValidation.TryValidate(annotation, out var error).ShouldBeTrue(error);

        annotation.Note.ShouldBe("duplicate provider notification race");
        annotation.TagFilter.ShouldBe("status=5*");
        annotation.SloDefinitionId.ShouldBeNull();
    }

    [TimedFact]
    public void TryValidate_SloNote_ClearsTheInstrumentFieldsAndTheAcknowledgedNoiseFlag()
    {
        var annotation = new MetricAnnotation
        {
            Target = MetricAnnotationTarget.Slo,
            SloDefinitionId = 3,
            Instrument = "c",
            TagFilter = "a=b",
            AcknowledgedNoise = true,
            Note = "Known",
            StartsAt = Start,
        };

        MetricAnnotationValidation.TryValidate(annotation, out var error).ShouldBeTrue(error);

        annotation.Instrument.ShouldBeNull();
        annotation.TagFilter.ShouldBeNull();
        annotation.AcknowledgedNoise.ShouldBeFalse();
    }

    [TimedTheory]
    [InlineData("empty-note", "Note")]
    [InlineData("long-note", "Note")]
    [InlineData("no-instrument", "Instrument")]
    [InlineData("bad-filter", "TagFilter")]
    [InlineData("slo-without-id", "SloDefinitionId")]
    [InlineData("bad-target", "Target")]
    [InlineData("no-start", "StartsAt")]
    [InlineData("end-before-start", "EndsAt")]
    [InlineData("end-equals-start", "EndsAt")]
    public void TryValidate_Invalid_IsRejectedWithAMessage(string scenario, string mentions)
    {
        var annotation = InstrumentNote();
        switch (scenario)
        {
            case "empty-note":
                annotation.Note = "   ";
                break;
            case "long-note":
                annotation.Note = new string('x', MetricAnnotationValidation.MaxNoteLength + 1);
                break;
            case "no-instrument":
                annotation.Instrument = " ";
                break;
            case "bad-filter":
                annotation.TagFilter = "status";
                break;
            case "slo-without-id":
                annotation.Target = MetricAnnotationTarget.Slo;
                break;
            case "bad-target":
                annotation.Target = (MetricAnnotationTarget)9;
                break;
            case "no-start":
                annotation.StartsAt = default;
                break;
            case "end-before-start":
                annotation.EndsAt = Start.AddMinutes(-1);
                break;
            default:
                annotation.EndsAt = Start;
                break;
        }

        MetricAnnotationValidation.TryValidate(annotation, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldContain(mentions);
    }

    [TimedFact]
    public void TryValidate_NoteAtTheLengthLimit_IsAccepted()
    {
        var annotation = InstrumentNote();
        annotation.Note = new string('x', MetricAnnotationValidation.MaxNoteLength);

        MetricAnnotationValidation.TryValidate(annotation, out var error).ShouldBeTrue(error);
    }

    [TimedFact]
    public void TryValidate_UnspecifiedKindInstants_AreReadAsUtc()
    {
        var annotation = InstrumentNote();
        annotation.StartsAt = DateTime.SpecifyKind(Start, DateTimeKind.Unspecified);
        annotation.EndsAt = DateTime.SpecifyKind(Start.AddHours(1), DateTimeKind.Unspecified);

        MetricAnnotationValidation.TryValidate(annotation, out _).ShouldBeTrue();

        annotation.StartsAt.Kind.ShouldBe(DateTimeKind.Utc);
        annotation.EndsAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        annotation.StartsAt.ShouldBe(Start);
    }

    [TimedTheory]
    [InlineData(null, MetricAnnotationValidation.AnonymousAuthor)]
    [InlineData("", MetricAnnotationValidation.AnonymousAuthor)]
    [InlineData("  ", MetricAnnotationValidation.AnonymousAuthor)]
    [InlineData(" alice ", "alice")]
    public void NormalizeAuthor_TrimsOrFallsBackToAnonymous(string? name, string expected)
    {
        MetricAnnotationValidation.NormalizeAuthor(name).ShouldBe(expected);
    }

    [TimedFact]
    public void NormalizeAuthor_OverlongName_IsCutToTheColumnLength()
    {
        MetricAnnotationValidation.NormalizeAuthor(new string('a', 300)).Length.ShouldBe(MetricAnnotationValidation.MaxAuthorLength);
    }

    [TimedTheory]
    [InlineData(null, null, true)]
    [InlineData(null, "status=500", true)]
    [InlineData("status=500", null, true)]
    [InlineData("status=500", "status=500", true)]
    [InlineData("status=500", "status=5*", true)]
    [InlineData("status=5*", "status=50*", true)]
    [InlineData("status=500", "status=404", false)]
    [InlineData("status=5*", "status=4*", false)]
    [InlineData("error=duplicate", "route=/pay", true)]
    [InlineData("status=500,route=/a", "route=/b", false)]
    [InlineData("status={none}", "status={none}", true)]
    [InlineData("status={none}", "status=*", false)]
    [InlineData("status={none}", "status=500", false)]
    public void AppliesTo_SameInstrument_FollowsTheFilterOverlap(string? noteFilter, string? tileFilter, bool expected)
    {
        var note = Model(noteFilter);
        var tile = new MetricBoardTile { Instrument = "http", TagFilter = tileFilter };

        MetricAnnotationMatch.AppliesTo(note, tile).ShouldBe(expected);
    }

    [TimedFact]
    public void AppliesTo_OtherInstrumentOrSloNote_NeverApplies()
    {
        var tile = new MetricBoardTile { Instrument = "http" };

        MetricAnnotationMatch.AppliesTo(Model(null, instrument: "other"), tile).ShouldBeFalse();
        MetricAnnotationMatch.AppliesTo(new MetricAnnotationModel { Target = MetricAnnotationTarget.Slo, SloDefinitionId = 1 }, tile).ShouldBeFalse();
    }

    [TimedTheory]
    [InlineData(SloState.Breaching, SloState.Acknowledged)]
    [InlineData(SloState.Warning, SloState.Acknowledged)]
    [InlineData(SloState.Healthy, SloState.Healthy)]
    [InlineData(SloState.NoData, SloState.NoData)]
    public void Acknowledge_ActiveAcknowledgedNoise_TurnsOnlyAProblemIntoAcknowledged(SloState status, SloState expected)
    {
        var note = Model(null, acknowledged: true);

        MetricAnnotationMatch.Acknowledge(status, [note], Start.AddHours(1)).ShouldBe(expected);
    }

    [TimedFact]
    public void Acknowledge_NoRuleOrPlainNote_LeavesTheStatus()
    {
        MetricAnnotationMatch.Acknowledge(null, [Model(null, acknowledged: true)], Start.AddHours(1)).ShouldBeNull();
        MetricAnnotationMatch.Acknowledge(SloState.Breaching, [Model(null)], Start.AddHours(1)).ShouldBe(SloState.Breaching);
    }

    [TimedTheory]
    [InlineData(-60, null, true)]
    [InlineData(-60, 60, true)]
    [InlineData(-60, 0, true)]
    [InlineData(-60, -1, false)]
    [InlineData(0, null, false)]
    [InlineData(1, null, false)]
    public void Acknowledge_NoteMustBeActiveAtTheWindowEnd(int startMinutes, int? endMinutes, bool acknowledged)
    {
        var end = Start.AddHours(1);
        var note = new MetricAnnotationModel
        {
            Target = MetricAnnotationTarget.Instrument,
            Instrument = "http",
            AcknowledgedNoise = true,
            StartsAt = end.AddMinutes(startMinutes),
            EndsAt = endMinutes is { } m ? end.AddMinutes(m) : null,
        };

        MetricAnnotationMatch.Acknowledge(SloState.Breaching, [note], end).ShouldBe(acknowledged ? SloState.Acknowledged : SloState.Breaching);
    }

    [TimedFact]
    public void MetricFilterOverlaps_EmptyFilter_OverlapsEverything()
    {
        MetricFilter.TryParse("status=500", out var narrow, out _).ShouldBeTrue();

        MetricFilter.Empty.Overlaps(narrow).ShouldBeTrue();
        narrow.Overlaps(MetricFilter.Empty).ShouldBeTrue();
    }

    private static MetricAnnotation InstrumentNote() => new()
    {
        Target = MetricAnnotationTarget.Instrument,
        Instrument = "http",
        Note = "Known race",
        StartsAt = Start,
    };

    private static MetricAnnotationModel Model(string? filter, string instrument = "http", bool acknowledged = false) => new()
    {
        Target = MetricAnnotationTarget.Instrument,
        Instrument = instrument,
        TagFilter = filter,
        AcknowledgedNoise = acknowledged,
        StartsAt = Start,
    };
}
