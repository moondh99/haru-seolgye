namespace Haru.Api.Config;

public sealed record HaruOptions
{
    public bool AiEnabled { get; init; } = true;
    public string ModelFast { get; init; } = "claude-haiku-5.5";
    public string ModelSmart { get; init; } = "claude-sonnet-5.5";
    public string EffortFast { get; init; } = "low";
    public string EffortSmart { get; init; } = "medium";
    public string CopilotHome { get; init; } = Path.Combine(Path.GetTempPath(), "haru-copilot");
    public string? CopilotToken { get; init; }
    public string McpUrl { get; init; } = "http://localhost:5258/mcp";
    public int PlansPerWindow { get; init; } = 8;
    public int WindowMinutes { get; init; } = 10;
    public int MaxConcurrent { get; init; } = 3;
    public int DailyCap { get; init; } = 250;
    public int PlanTimeoutSeconds { get; init; } = 120;
    public int AgentTimeoutSeconds { get; init; } = 60;
    public int CritiqueBudgetSeconds { get; init; } = 70;
    public int BufferMinutes { get; init; } = 5;
    public double MaxLoad { get; init; } = 0.85;
    public int MaxInputChars { get; init; } = 3000;
    public string Version { get; init; } = "1.0.0";

    public static HaruOptions From(IConfiguration c)
    {
        static int Int(string? v, int d) => int.TryParse(v, out var n) && n > 0 ? n : d;
        static string Str(string? v, string d) => string.IsNullOrWhiteSpace(v) ? d : v.Trim();

        var token = c["COPILOT_GITHUB_TOKEN"];
        if (string.IsNullOrWhiteSpace(token) || token.Trim().Equals("unset", StringComparison.OrdinalIgnoreCase))
            token = null;

        var mcp = c["MCP_URL"];
        if (string.IsNullOrWhiteSpace(mcp))
        {
            var discovered = c["services:mcp:http:0"];
            mcp = string.IsNullOrWhiteSpace(discovered) ? "http://localhost:5258" : discovered;
        }
        mcp = mcp.TrimEnd('/');
        if (!mcp.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase)) mcp += "/mcp";

        var d = new HaruOptions();
        return d with
        {
            AiEnabled = !string.Equals(c["HARU_AI_ENABLED"], "false", StringComparison.OrdinalIgnoreCase),
            ModelFast = Str(c["HARU_MODEL_FAST"], d.ModelFast),
            ModelSmart = Str(c["HARU_MODEL_SMART"], d.ModelSmart),
            EffortFast = Str(c["HARU_EFFORT_FAST"], d.EffortFast),
            EffortSmart = Str(c["HARU_EFFORT_SMART"], d.EffortSmart),
            CopilotHome = Str(c["HARU_COPILOT_HOME"], d.CopilotHome),
            CopilotToken = token?.Trim(),
            McpUrl = mcp,
            PlansPerWindow = Int(c["HARU_PLANS_PER_WINDOW"], d.PlansPerWindow),
            WindowMinutes = Int(c["HARU_WINDOW_MINUTES"], d.WindowMinutes),
            MaxConcurrent = Int(c["HARU_MAX_CONCURRENT"], d.MaxConcurrent),
            DailyCap = Int(c["HARU_DAILY_CAP"], d.DailyCap),
            PlanTimeoutSeconds = Int(c["HARU_PLAN_TIMEOUT_SECONDS"], d.PlanTimeoutSeconds),
            AgentTimeoutSeconds = Int(c["HARU_AGENT_TIMEOUT_SECONDS"], d.AgentTimeoutSeconds),
        };
    }
}
