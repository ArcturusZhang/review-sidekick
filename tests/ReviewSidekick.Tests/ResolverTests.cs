using System.Text.Json;
using GitHub.Copilot;

namespace ReviewSidekick.Tests;

public sealed class ResolverTests
{
    [Fact]
    public void BrandingMatchesExecutableAndNamespace()
    {
        Assert.Equal("review-sidekick", typeof(Options).Assembly.GetName().Name);
        Assert.Equal("ReviewSidekick", typeof(Options).Namespace);
        Assert.Contains("Review Sidekick", Options.Help);
        Assert.Contains("review-sidekick plan", Options.Help);
        Assert.Contains("~/.review-sidekick/runs/", Options.Help);
    }

    [Theory]
    [InlineData("https://github.com/Azure/azure-sdk-for-net/pull/42", "github.com", "Azure/azure-sdk-for-net", 42)]
    [InlineData("https://github.example.com/team/sdk/pull/123/", "github.example.com", "team/sdk", 123)]
    public void ParsesPrUrls(string url, string host, string repo, int number)
    {
        var pr = PullRequestId.Parse(url);
        Assert.Equal(host, pr.Host);
        Assert.Equal(repo, pr.Repo);
        Assert.Equal(number, pr.Number);
    }

    [Theory]
    [InlineData("http://github.com/Azure/sdk/pull/42")]
    [InlineData("https://user:secret@github.com/Azure/sdk/pull/42")]
    [InlineData("https://github.com/Azure/sdk/issues/42")]
    [InlineData("https://github.com/Azure/sdk/pull/0")]
    [InlineData("https://github.com/Azure/sdk/pull/2147483648")]
    [InlineData("https://github.com/Azure/sdk/pull/42?token=secret")]
    [InlineData("https://github.com:1234/Azure/sdk/pull/42")]
    [InlineData("https://github.com/-owner/sdk/pull/42")]
    public void RejectsInvalidPrUrls(string url) =>
        Assert.Throws<ArgumentException>(() => PullRequestId.Parse(url));

    [Fact]
    public void PlanIsDefaultAndOptionsPreservePaths()
    {
        var options = Options.Parse(["--sdk-pr", "url", "--sdk-repo", @"C:\a b\sdk", "--spec-repo", @"C:\spec"]);
        Assert.Equal(Phase.Plan, options.Phase);
        Assert.Equal(@"C:\a b\sdk", options.SdkRepo);
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("publish")]
    [InlineData("review")]
    public void ContinuationsRequireOnlyRunDirectory(string command)
    {
        Assert.Throws<ArgumentException>(() => Options.Parse([command]));
        Assert.Throws<ArgumentException>(() => Options.Parse([command, "--run-dir", "run", "--model", "model"]));
        Assert.Equal("run", Options.Parse([command, "--run-dir", "run"]).RunDirectory);
    }

    [Fact]
    public void RejectsUnknownAndRepeatedOptions()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(["--unknown", "value"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--sdk-pr", "url", "--sdk-pr", "url"]));
    }

    [Fact]
    public void SnapshotFingerprintIncludesEditedAndNewComments()
    {
        var initial = Snapshot();
        var edited = initial with { ConversationComments = [initial.ConversationComments[0] with { Body = "new text" }] };
        var appended = initial with { ConversationComments = [.. initial.ConversationComments, initial.ConversationComments[0] with { Id = 2 }] };
        Assert.Equal(initial.Fingerprint, Snapshot().Fingerprint);
        Assert.NotEqual(initial.Fingerprint, edited.Fingerprint);
        Assert.NotEqual(initial.Fingerprint, appended.Fingerprint);
        var roundTrip = JsonSerializer.Deserialize<PrSnapshot>(JsonSerializer.Serialize(initial, JsonSettings.Compact), JsonSettings.Compact)!;
        Assert.Equal(initial.Fingerprint, roundTrip.Fingerprint);
    }

    [Fact]
    public void PlanDoesNotExposeShellWritesMcpOrSkillExecution()
    {
        var config = Agent.Configure(Phase.Plan, State());
        Assert.Equal(Agent.ReadTools, config.AvailableTools);
        Assert.Null(config.McpServers);
        Assert.False(config.EnableSkills);
        Assert.False(config.EnableConfigDiscovery);
        Assert.False(config.EnableFileHooks);
        Assert.False(config.EnableSessionStore);
    }

    [Fact]
    public void ApplyExposesOnlySelectedGeneratorTools()
    {
        var config = Agent.Configure(Phase.Apply, State());
        var server = Assert.IsType<McpStdioServerConfig>(config.McpServers!["generator-agent"]);
        Assert.Equal(Agent.GeneratorTools, server.Tools);
        Assert.DoesNotContain("batch_fix", server.Tools!);
        Assert.DoesNotContain("pregen_cleanup", server.Tools!);
        Assert.DoesNotContain("commit_iteration", server.Tools!);
        Assert.Contains("skill", config.AvailableTools!);
        Assert.Contains("mcp:generator-agent-run_code_generation", config.AvailableTools!);
    }

    [Fact]
    public void ReviewDoesNotExposeFileEditingOrGeneratorTools()
    {
        var config = Agent.Configure(Phase.Review, State());
        Assert.Null(config.McpServers);
        Assert.DoesNotContain("apply_patch", config.AvailableTools!);
        Assert.DoesNotContain("skill", config.AvailableTools!);
    }

    [Fact]
    public void PhasesCannotSkipPrerequisitesOrReplay()
    {
        var state = State();
        Assert.Throws<InvalidOperationException>(() => state.CheckNextPhase(Phase.Apply));
        state.Attempts.Add(new(Phase.Plan, DateTimeOffset.UtcNow, "session", Result("blocked")));
        Assert.Throws<InvalidOperationException>(() => state.CheckNextPhase(Phase.Apply));
        state.Attempts[0] = state.Attempts[0] with { Result = Result("ready") };
        state.CheckNextPhase(Phase.Apply);
        Assert.Throws<InvalidOperationException>(() => state.CheckNextPhase(Phase.Publish));
        state.Attempts.Add(new(Phase.Apply, DateTimeOffset.UtcNow, "session", null));
        Assert.Throws<InvalidOperationException>(() => state.CheckNextPhase(Phase.Apply));
        Assert.Throws<InvalidOperationException>(() => state.CheckNextPhase(Phase.Publish));
    }

    [Fact]
    public void InvalidAgentResultDoesNotAdvance()
    {
        Assert.Throws<InvalidOperationException>(() => RunState.ValidateResult(Result("success")));
        Assert.Throws<InvalidOperationException>(() => RunState.ValidateResult(Result("ready") with { Summary = "" }));
        RunState.ValidateResult(Result("ready"));
        Assert.Throws<InvalidOperationException>(() => RunState.ValidateResult(Result("ready"), Phase.Apply));
        Assert.Throws<InvalidOperationException>(() => RunState.ValidateResult(
            Result("ready") with { Validation = ["dotnet build: passed"] }, Phase.Publish));
    }

    [Fact]
    public void RunStateCannotBeStoredInsideCheckouts()
    {
        var root = Path.Combine(Path.GetTempPath(), "sdk-root");
        Assert.Throws<ArgumentException>(() => RunStore.EnsureOutside(root, root));
        Assert.Throws<ArgumentException>(() => RunStore.EnsureOutside(Path.Combine(root, ".runs"), root));
        RunStore.EnsureOutside(root + "-runs", root);
    }

    [Fact]
    public async Task RunStateRoundTripsAndLocksOutConcurrentWriters()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"review-sidekick-tests-{Guid.NewGuid():N}");
        try
        {
            var store = new RunStore(directory);
            using var runLock = store.Lock();
            Assert.Throws<IOException>(() => store.Lock());
            await store.SaveAsync(State(), CancellationToken.None);
            var loaded = await store.LoadAsync(CancellationToken.None);
            Assert.Equal(State().Snapshot.Fingerprint, loaded.Snapshot.Fingerprint);
            Assert.False(File.Exists(Path.Combine(directory, "run.json.tmp")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OldRunStateRequiresFreshPlanBeforeSnapshotDeserialization()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"review-sidekick-tests-{Guid.NewGuid():N}");
        try
        {
            var store = new RunStore(directory);
            using var runLock = store.Lock();
            await File.WriteAllTextAsync(Path.Combine(directory, "run.json"),
                """{"version":1,"snapshot":{"metadata":{}}}""");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync(CancellationToken.None));
            Assert.Contains("Start a fresh plan", error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static PrSnapshot Snapshot() => new(
        new PrMetadata("https://github.com/Azure/sdk/pull/42", "SDK PR", null, true,
            "abc", "feature", "user/sdk", "main", "Azure/sdk", true),
        [], [new PrComment(1, "IC_1", "rename this", "https://github.com/Azure/sdk/pull/42#issuecomment-1",
            "reviewer", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), null)], [], [], []);

    internal static RunState State() => new()
    {
        PrUrl = "https://github.com/Azure/sdk/pull/42",
        SdkRepo = Path.GetFullPath("sdk"),
        SpecRepo = Path.GetFullPath("specs"),
        SkillHash = "hash",
        Snapshot = Snapshot()
    };

    private static PhaseResult Result(string status) => new(status, "summary", [], [], null, null);
}
