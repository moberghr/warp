using System.Text;
using Shouldly;
using Warp.Http.Observability;

namespace Warp.Tests.ClientObservability;

/// <summary>
/// The byte-budget truncation the public client-ingest endpoint applies to every caller-supplied string. It
/// used to shorten one char at a time and re-count the whole prefix each step, quadratic in the input.
/// </summary>
[Trait("Category", "NoDb")]
public class PrefixWithinBytesTests
{
    [TimedTheory]
    [InlineData("", 0, "")]
    [InlineData("abc", 3, "abc")]
    [InlineData("abc", 2, "ab")]
    [InlineData("abc", 0, "")]
    [InlineData("aé", 2, "a")] // é is 2 bytes: does not fit after "a"
    [InlineData("aé", 3, "aé")]
    [InlineData("a€b", 3, "a")] // € is 3 bytes
    [InlineData("a€b", 4, "a€")]
    [InlineData("a😀b", 4, "a")] // the emoji is a surrogate pair, 4 bytes: never split
    [InlineData("a😀b", 5, "a😀")]
    public void PrefixWithinBytes_CutsOnACharacterBoundary(string value, int maxBytes, string expected)
    {
        HttpCaptureHelpers.PrefixWithinBytes(value, maxBytes).ShouldBe(expected);
    }

    [TimedFact]
    public void PrefixWithinBytes_LoneSurrogate_CountsAsTheReplacementCharacter()
    {
        var value = "a\uD800b";

        HttpCaptureHelpers.PrefixWithinBytes(value, 3).ShouldBe("a");
        HttpCaptureHelpers.PrefixWithinBytes(value, 4).ShouldBe("a\uD800");
    }

    [TimedFact]
    public void PrefixWithinBytes_MatchesTheEncoderOnMixedInput()
    {
        var value = string.Concat(Enumerable.Repeat("aé€😀", 50));

        for (var max = 0; max <= Encoding.UTF8.GetByteCount(value); max++)
        {
            var prefix = HttpCaptureHelpers.PrefixWithinBytes(value, max);

            Encoding.UTF8.GetByteCount(prefix).ShouldBeLessThanOrEqualTo(max);
            value.ShouldStartWith(prefix);

            // Longest: the next whole character would not have fit.
            if (prefix.Length < value.Length)
            {
                var next = char.IsHighSurrogate(value[prefix.Length]) ? 2 : 1;
                Encoding.UTF8.GetByteCount(value.AsSpan(0, prefix.Length + next)).ShouldBeGreaterThan(max);
            }
        }
    }

    [TimedFact]
    public void PrefixWithinBytes_LargeMultiByteInput_IsLinear()
    {
        // 4 MB of 3-byte characters cut to 1 byte: the old per-char re-count did ~1.4M full scans here.
        var value = new string('€', 1_400_000);

        HttpCaptureHelpers.PrefixWithinBytes(value, 1).ShouldBe(string.Empty);
        HttpCaptureHelpers.PrefixWithinBytes(value, 3_000_000).Length.ShouldBe(1_000_000);
    }
}
