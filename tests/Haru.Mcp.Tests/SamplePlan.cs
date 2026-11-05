using System.Text.Json;

namespace Haru.Mcp.Tests;

/// <summary>The sample plan owned by Haru.Api; the test project copies the file next to its binaries.</summary>
internal sealed record SamplePlan(
    string Day,
    string WorkStart,
    string WorkEnd,
    List<ScheduleBlock> Blocks,
    List<ExportBlock> ExportBlocks,
    bool Valid,
    double LoadRatio,
    int OccupiedMinutes,
    int AvailableMinutes)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static SamplePlan Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "sample-plan.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var blocks = root.GetProperty("blocks");
        var validation = root.GetProperty("validation");
        return new SamplePlan(
            root.GetProperty("day").GetString()!,
            root.GetProperty("workStart").GetString()!,
            root.GetProperty("workEnd").GetString()!,
            blocks.Deserialize<List<ScheduleBlock>>(Options)!,
            blocks.Deserialize<List<ExportBlock>>(Options)!,
            validation.GetProperty("valid").GetBoolean(),
            validation.GetProperty("loadRatio").GetDouble(),
            validation.GetProperty("occupiedMinutes").GetInt32(),
            validation.GetProperty("availableMinutes").GetInt32());
    }
}
