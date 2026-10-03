namespace SqlAgent.Core;

/// <summary>
/// The bounds on one generated query. Shared by the guardrail (which injects <c>TOP (MaxRows + 1)</c>) and the
/// executor (which reads at most <see cref="MaxRows"/> rows and at most <see cref="MaxResultBytes"/>), so the two
/// can never disagree about the cap.
/// </summary>
/// <param name="MaxRows">Rows returned to the caller. One more is read to learn whether the result was cut.</param>
/// <param name="MaxResultBytes">
/// An approximate cap on the whole result's size, independent of <see cref="MaxRows"/>. A row count alone does not
/// bound a result: <c>STRING_AGG</c> or <c>MAX</c> with no <c>GROUP BY</c> returns the whole allowed table
/// concatenated or scanned into a single row, and <c>REPLICATE</c>/<c>SPACE</c> can make one cell itself huge.
/// Default 2 MB comfortably covers 500 ordinary rows while still catching those cases.
/// </param>
/// <param name="CommandTimeout">Server-side time limit for one query.</param>
public sealed record QueryLimits(int MaxRows = 500, long MaxResultBytes = 2 * 1024 * 1024, TimeSpan CommandTimeout = default)
{
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(15);

    public int MaxRows { get; } = MaxRows > 0 ? MaxRows : throw new ArgumentOutOfRangeException(nameof(MaxRows));

    public long MaxResultBytes { get; } = MaxResultBytes > 0 ? MaxResultBytes : throw new ArgumentOutOfRangeException(nameof(MaxResultBytes));

    public TimeSpan CommandTimeout { get; } = CommandTimeout == default ? DefaultCommandTimeout : CommandTimeout;
}
