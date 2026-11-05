using System.ComponentModel;

namespace Haru.Mcp;

/// <summary>Either a value or a Korean error message; used instead of exceptions for bad input.</summary>
public readonly record struct Outcome<T>(T? Value, string? Error)
{
    public bool IsOk => Error is null;

    public static Outcome<T> Ok(T value) => new(value, null);

    public static Outcome<T> Fail(string error) => new(default, error);
}

// Tool results. Property names serialize to camelCase and match docs/api-contract.md section 3.

public sealed record ResolveDateResult(bool Ok, string? Date, string? Time, string? Normalized, string? Error);

public sealed record FreeSlot(string Start, string End, int Minutes);

public sealed record FreeSlotsResult(IReadOnlyList<FreeSlot> Slots, int TotalFreeMinutes);

public sealed record Violation(string Code, string Message, IReadOnlyList<string> BlockIds);

public sealed record ValidationReport(
    bool Valid,
    double LoadRatio,
    int OccupiedMinutes,
    int AvailableMinutes,
    IReadOnlyList<Violation> Violations);

public sealed record IcsResult(string Filename, string Ics, int EventCount);

// Tool inputs. Members are nullable so that malformed callers get a Korean error instead of a binding failure.

public sealed class TimeRange
{
    [Description("Start time, HH:mm (24-hour, KST).")]
    public string? Start { get; init; }

    [Description("End time, HH:mm (24-hour, KST), later than start.")]
    public string? End { get; init; }
}

public sealed class ScheduleBlock
{
    [Description("Block id, for example b1.")]
    public string? Id { get; init; }

    [Description("Block title.")]
    public string? Title { get; init; }

    [Description("Start time, HH:mm (24-hour, KST).")]
    public string? Start { get; init; }

    [Description("End time, HH:mm (24-hour, KST), later than start.")]
    public string? End { get; init; }

    [Description("Block kind: task, fixed or break.")]
    public string? Kind { get; init; }

    [Description("Optional same-day deadline, HH:mm. Only checked for task blocks.")]
    public string? DeadlineTime { get; init; }
}

public sealed class ExportBlock
{
    [Description("Block id, for example b1. Used to build the event UID.")]
    public string? Id { get; init; }

    [Description("Block title, used as the event summary.")]
    public string? Title { get; init; }

    [Description("Start time, HH:mm (24-hour, KST).")]
    public string? Start { get; init; }

    [Description("End time, HH:mm (24-hour, KST), later than start.")]
    public string? End { get; init; }

    [Description("Block kind: task, fixed or break. Break blocks are not exported.")]
    public string? Kind { get; init; }

    [Description("Optional one-sentence reason, added to the event description.")]
    public string? Reason { get; init; }
}
