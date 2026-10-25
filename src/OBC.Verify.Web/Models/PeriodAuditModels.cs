using OBC.Verify.Web.Services.Standard;

namespace OBC.Verify.Web.Models;

public sealed class PeriodAuditRequest
{
    public SolanaNetwork Network { get; set; } = SolanaNetwork.MainnetBeta;
    /// <summary>The publisher wallet P whose books are rebuilt.</summary>
    public string Publisher { get; set; } = string.Empty;
    /// <summary>A transaction of the latest close of the latest period. Optional: without it the audit starts from the period that ends last.</summary>
    public string? LatestCloseSignature { get; set; }
    /// <summary>Extra https bases to fetch off-chain files from, tried after the location account's.</summary>
    public List<string> ExtraBases { get; set; } = new();
    /// <summary>Local label map: <c>label:name</c> bases of the location account resolved to https bases.</summary>
    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Bounds for the period audit (configuration section "Verification:Audit").</summary>
public sealed class PeriodAuditOptions
{
    /// <summary>Pages of 1000 signatures read from the publisher's history before stopping with "history incomplete".</summary>
    public int MaxPages { get; set; } = 50;
    /// <summary>Transactions and files read in parallel.</summary>
    public int Concurrency { get; set; } = 4;
}

public enum PeriodAuditStatus
{
    Complete,
    Failed,
    Open,
    /// <summary>The period's latest version is a tombstone: it counts as never closed.</summary>
    Voided
}

public enum AuditFindingKind
{
    /// <summary>Published into the period while it was closed (a document that wasn't a member).</summary>
    LateEntry,
    /// <summary>Published while the period was open, but missing from the close that followed.</summary>
    Omitted,
    /// <summary>A member got a newer version while the period was closed.</summary>
    ChangedAfterClose,
    /// <summary>A listed member can't be found, has a different hash or date, or isn't signed by the publisher.</summary>
    BrokenMember,
    /// <summary>A version of the period record breaks the type's rules (data, count, root, versioning).</summary>
    InvalidClose,
    /// <summary>A previous-period link can't be followed.</summary>
    BrokenLink,
    PeriodGap,
    PeriodOverlap
}

public sealed class AuditFinding
{
    public AuditFindingKind Kind { get; init; }
    public string Detail { get; init; } = string.Empty;
    /// <summary>The transaction the finding is about, when there is one.</summary>
    public string? Signature { get; init; }

    public string Title => Kind switch
    {
        AuditFindingKind.LateEntry => "Late entry",
        AuditFindingKind.Omitted => "Omitted",
        AuditFindingKind.ChangedAfterClose => "Changed after close",
        AuditFindingKind.BrokenMember => "Broken member",
        AuditFindingKind.InvalidClose => "Invalid period record",
        AuditFindingKind.BrokenLink => "Broken previous-period link",
        AuditFindingKind.PeriodGap => "Period gap",
        AuditFindingKind.PeriodOverlap => "Period overlap",
        _ => Kind.ToString()
    };
}

/// <summary>One version of a period record: a close or a reopen.</summary>
public sealed class PeriodAuditVersion
{
    public long Number { get; init; }
    public string Signature { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
    public ulong Slot { get; init; }
    public DateTimeOffset? BlockTime { get; init; }
    /// <summary>closed, reopened or voided; null when the file couldn't be read.</summary>
    public string? State { get; init; }
    public string? Reason { get; init; }
    public long? Count { get; init; }
}

public sealed class PeriodAuditPeriod
{
    public string DocumentId { get; init; } = string.Empty;
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string Range => $"{PeriodClose.FormatDate(From)} – {PeriodClose.FormatDate(To)}";
    public PeriodAuditStatus Status { get; set; }
    public List<PeriodAuditVersion> Versions { get; } = new();
    public int TimesClosed => Versions.Count(v => v.State == PeriodClose.ClosedState);
    public int TimesReopened => Versions.Count(v => v.State == PeriodClose.ReopenedState);
    /// <summary>Members of the latest close, null when the period has no valid close.</summary>
    public long? MemberCount { get; set; }
    /// <summary>Reached by following previous-period links from the starting close.</summary>
    public bool InLinkChain { get; set; }
    public List<AuditFinding> Findings { get; } = new();
}

public sealed class PeriodAuditReport
{
    public string Publisher { get; set; } = string.Empty;
    public string? Network { get; set; }
    /// <summary>Set when the audit couldn't run at all.</summary>
    public string? Error { get; set; }
    /// <summary>The https bases files were fetched from, in order.</summary>
    public List<string> Bases { get; } = new();
    public int ScannedSignatures { get; set; }
    /// <summary>v3 records signed by the publisher, period records included.</summary>
    public int RecordCount { get; set; }
    /// <summary>False when the publisher's history was only partly read: records may be missing from every check.</summary>
    public bool HistoryComplete { get; set; }
    /// <summary>Records whose file couldn't be fetched or read, so they couldn't be placed in a period.</summary>
    public int UnreadRecords { get; set; }
    /// <summary>Record versions whose booking date falls in no period.</summary>
    public int OutsidePeriods { get; set; }
    /// <summary>Ordered by start date.</summary>
    public List<PeriodAuditPeriod> Periods { get; } = new();
    /// <summary>Gaps, overlaps and broken links between periods.</summary>
    public List<AuditFinding> ChainFindings { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>At least one period is complete, and every period is complete or voided, with no gaps or overlaps.</summary>
    public bool AllComplete => Error is null && ChainFindings.Count == 0 && Periods.Any(p => p.Status == PeriodAuditStatus.Complete)
                               && Periods.All(p => p.Status is PeriodAuditStatus.Complete or PeriodAuditStatus.Voided);
}
