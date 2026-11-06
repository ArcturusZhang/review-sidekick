using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReviewSidekick;

internal static class JsonSettings
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    public static readonly JsonSerializerOptions Pretty = new(Compact) { WriteIndented = true };
}

internal sealed record ItemResult(string SourceId, string Status, string Explanation, string Evidence);
internal sealed record PhaseResult(
    string Status, string Summary, ItemResult[] Items, string[] Validation,
    string? SpecPrUrl, string? SdkPrUrl);
internal sealed record PhaseAttempt(
    Phase Phase, DateTimeOffset StartedAt, string SessionId, PhaseResult? Result);

internal sealed class RunState
{
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    public required string SdkRepo { get; set; }
    public required string SpecRepo { get; set; }
    public required string PrUrl { get; set; }
    public required string SkillHash { get; set; }
    public string? Model { get; set; }
    public string? CopilotPath { get; set; }
    public required PrSnapshot Snapshot { get; set; }
    public List<PhaseAttempt> Attempts { get; set; } = [];

    public void CheckNextPhase(Phase phase)
    {
        var required = phase switch
        {
            Phase.Apply => Phase.Plan,
            Phase.Publish => Phase.Apply,
            Phase.Review => Phase.Publish,
            _ => (Phase?)null
        };
        if (Attempts.Any(a => a.Phase == phase))
            throw new InvalidOperationException(
                "This phase has already been attempted. Inspect its output and any partial changes; start a fresh plan rather than replaying mutations.");
        if (required is not null &&
            !Attempts.Any(a => a.Phase == required && a.Result?.Status == "ready"))
            throw new InvalidOperationException($"A ready {required} result is required before {phase}.");
    }

    public static void ValidateResult(PhaseResult result, Phase phase = Phase.Plan)
    {
        if (result.Status is not ("ready" or "blocked") || string.IsNullOrWhiteSpace(result.Summary) ||
            result.Items is null || result.Validation is null ||
            result.Items.Any(i => i is null || string.IsNullOrWhiteSpace(i.SourceId) ||
                string.IsNullOrWhiteSpace(i.Status) || string.IsNullOrWhiteSpace(i.Explanation) ||
                string.IsNullOrWhiteSpace(i.Evidence)))
            throw new InvalidOperationException("Agent returned an incomplete or invalid phase result.");
        if (result.Status == "ready" && phase is Phase.Apply or Phase.Publish &&
            result.Validation.Length == 0)
            throw new InvalidOperationException("Implementation/publication requires a nonempty validation report.");
        if (result.Status == "ready" && phase == Phase.Publish &&
            string.IsNullOrWhiteSpace(result.SdkPrUrl))
            throw new InvalidOperationException("Publication requires the existing SDK PR URL.");
    }
}

internal sealed class RunStore
{
    public string DirectoryPath { get; }
    public RunStore(string path) => DirectoryPath = Path.GetFullPath(path);
    private string StatePath => Path.Combine(DirectoryPath, "run.json");

    public FileStream Lock()
    {
        Directory.CreateDirectory(DirectoryPath);
        return new FileStream(Path.Combine(DirectoryPath, ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public async Task SaveAsync(RunState state, CancellationToken token)
    {
        var temporary = StatePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonSettings.Pretty), token);
        File.Move(temporary, StatePath, overwrite: true);
    }

    public async Task<RunState> LoadAsync(CancellationToken token)
    {
        var content = await File.ReadAllTextAsync(StatePath, token);
        var header = JsonSerializer.Deserialize<StateHeader>(content, JsonSettings.Pretty)
            ?? throw new InvalidOperationException("Run state is empty.");
        if (header.Version != RunState.CurrentVersion)
            throw new InvalidOperationException(
                $"Unsupported run state version: {header.Version}. Start a fresh plan for typed GitHub snapshots.");
        return JsonSerializer.Deserialize<RunState>(content, JsonSettings.Pretty)
            ?? throw new InvalidOperationException("Run state is empty.");
    }

    private sealed record StateHeader(int Version);

    public static void EnsureOutside(string path, params string[] repositories)
    {
        var full = Path.GetFullPath(path);
        foreach (var repository in repositories)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(repository), full);
            if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new ArgumentException("Run state must be outside the SDK and specification checkouts.");
        }
    }
}
