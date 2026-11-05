using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haru.Api.Contracts;

public static class HaruJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Lenient = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

public sealed record TaskItem(
    string Id, string Title, int Minutes, int Priority, string? Deadline,
    string? FixedStart, string? FixedEnd, string Kind, string? Note, string? DurationSource = null);

public sealed record Block(string Id, string? TaskId, string Title, string Start, string End, string Kind, string Reason);

public sealed record Violation(string Code, string Message, string[] BlockIds);

public sealed record ValidationReport(
    bool Valid, double LoadRatio, int OccupiedMinutes, int AvailableMinutes, Violation[] Violations);

public sealed record Deferred(string TaskId, string Title, string Reason);

public sealed record Risk(string Title, string Mitigation, string Severity);

public sealed record ModelPair(string Fast, string Smart);

public sealed record PlanMeta(
    string Source, bool AiGenerated, int Iterations, bool Repaired, long ElapsedMs, ModelPair? Models);

public sealed record PlanResult(
    string Day, string WorkStart, string WorkEnd, TaskItem[] Tasks, Block[] Blocks, Deferred[] Deferred,
    Risk[] Risks, string Summary, ValidationReport Validation, PlanMeta Meta);

public sealed record PlanRequest(
    string? Text, string? Day, string? WorkStart, string? WorkEnd, string? LunchStart, string? LunchEnd);

public sealed record ExportRequest(
    bool? Confirmed, string? Day, Block[]? Blocks, string? WorkStart, string? WorkEnd);

public sealed record SampleEvent(int T, string Event, JsonElement Data);

public sealed record SampleResponse(string InputText, PlanResult Plan, IReadOnlyList<SampleEvent>? Events = null);

public sealed record SampleRun(List<SampleEvent> Events);
