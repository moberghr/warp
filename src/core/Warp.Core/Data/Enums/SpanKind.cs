namespace Warp.Core.Enums;

/// <summary>
/// The role of a span in a trace (§8.28), mirroring <see cref="System.Diagnostics.ActivityKind"/> / OTel
/// SpanKind. Values from 1 (§8.11).
/// </summary>
public enum SpanKind
{
    Internal = 1,
    Server = 2,
    Client = 3,
    Producer = 4,
    Consumer = 5,
}
