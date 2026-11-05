using Haru.Api.Config;

namespace Haru.Api.Agents;

public enum ModelTier { Fast, Smart }

public sealed record AgentSpec(
    string Id, string Role, string PromptFile, ModelTier Tier, string[] Tools, string? Effort = null);

/// <summary>One place that defines every agent: role, instruction file, model tier and the tools it may call.</summary>
public sealed class AgentCatalog
{
    public const string FindFreeSlots = "find_free_slots";
    public const string ValidateSchedule = "validate_schedule";

    private readonly HaruOptions _options;
    private readonly AgentSpec[] _specs;

    public AgentCatalog(HaruOptions options)
    {
        _options = options;
        _specs =
        [
            new("intake", "입력 정리: 흩어진 메모에서 할 일을 뽑습니다.", "intake.md", ModelTier.Fast, []),
            new("estimator", "시간 추정: 각 할 일의 소요 시간을 추정합니다.", "estimator.md", ModelTier.Fast, []),
            new("prioritizer", "우선순위: 마감과 중요도로 순서를 정합니다.", "prioritizer.md", ModelTier.Fast, []),
            new("scheduler", "시간표: 빈 시간에 할 일을 배치하고 도구로 검증합니다.", "scheduler.md", ModelTier.Smart,
                [FindFreeSlots, ValidateSchedule], options.EffortSmart),
            new("critic", "비판: 계획이 실패할 이유를 미리 찾습니다.", "critic.md", ModelTier.Smart, [], options.EffortFast),
            new("coach", "코치: 하루 계획을 한 단락으로 요약합니다.", "coach.md", ModelTier.Fast, []),
        ];
    }

    public IReadOnlyList<AgentSpec> All => _specs;

    public AgentSpec Get(string id) =>
        _specs.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException($"Unknown agent: {id}", nameof(id));

    public string ModelFor(AgentSpec spec) => spec.Tier == ModelTier.Fast ? _options.ModelFast : _options.ModelSmart;

    public string EffortFor(AgentSpec spec) =>
        spec.Effort ?? (spec.Tier == ModelTier.Fast ? _options.EffortFast : _options.EffortSmart);
}

/// <summary>Reads instruction files once. A missing file stops start-up, so a bad deploy is visible at once.</summary>
public sealed class PromptLoader
{
    private readonly Dictionary<string, string> _cache = new();

    public PromptLoader(AgentCatalog catalog, string? directory = null)
    {
        var dir = directory ?? FindPromptDirectory();
        foreach (var spec in catalog.All)
            _cache[spec.PromptFile] = File.ReadAllText(Path.Combine(dir, spec.PromptFile));
    }

    public string Get(string file) => _cache[file];

    private static string FindPromptDirectory()
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var candidate = Path.Combine(root, "prompts");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("prompts directory not found next to the application");
    }
}
