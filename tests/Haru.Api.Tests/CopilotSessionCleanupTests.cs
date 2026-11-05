using Haru.Api.Config;
using Haru.Api.Copilot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Haru.Api.Tests;

// Copilot session events hold prompt text. The host must not keep them on disk.
public class CopilotSessionCleanupTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "haru-cleanup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private CopilotHost Host() => new(new HaruOptions { CopilotHome = _home }, NullLogger<CopilotHost>.Instance);

    [Fact]
    public void Clear_removes_sessions_left_by_an_earlier_process()
    {
        var old = Path.Combine(_home, "session-state", "old-session");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "events.jsonl"), "{}");
        var other = Path.Combine(_home, "installed-plugins");
        Directory.CreateDirectory(other);

        Host().ClearSessionState();

        Assert.False(Directory.Exists(Path.Combine(_home, "session-state")));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void Clear_does_nothing_when_no_sessions_exist()
    {
        Directory.CreateDirectory(_home);

        Host().ClearSessionState();

        Assert.True(Directory.Exists(_home));
    }

    [Fact]
    public async Task Delete_without_a_started_client_does_not_throw()
    {
        await Host().DeleteSessionAsync("missing-session");
    }
}
