using System.Text.Json;

namespace ReviewSidekick;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"] || args.Length == 0)
        {
            Console.WriteLine(Options.Help);
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            return await ExecuteAsync(Options.Parse(args), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled. Inspect any partial work before starting another run.");
            return 130;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(Options options, CancellationToken token)
    {
        ICommandRunner commands = new CommandRunner();
        var directory = options.RunDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".review-sidekick", "runs", Guid.NewGuid().ToString("N"));
        var store = new RunStore(directory);
        if (options.Phase == Phase.Plan)
            RunStore.EnsureOutside(store.DirectoryPath, options.SdkRepo!, options.SpecRepo!);
        using var runLock = store.Lock();
        RunState state;
        string skill;
        if (options.Phase == Phase.Plan)
        {
            if (File.Exists(Path.Combine(store.DirectoryPath, "run.json")))
                throw new InvalidOperationException("Run directory already contains state. Use a new directory.");
            var sdk = await RepositoryChecks.RootAsync(options.SdkRepo!, commands, token);
            var specs = await RepositoryChecks.RootAsync(options.SpecRepo!, commands, token);
            if (string.Equals(sdk, specs, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("SDK and specification checkouts must be distinct.");
            skill = await File.ReadAllTextAsync(RepositoryChecks.SkillPath(sdk), token);
            var pr = PullRequestId.Parse(options.Pr!);
            state = new RunState
            {
                PrUrl = pr.Url,
                SdkRepo = sdk,
                SpecRepo = specs,
                Model = options.Model,
                CopilotPath = options.CopilotPath,
                SkillHash = RepositoryChecks.SkillHash(skill),
                Snapshot = await GitHub.ReadAuthenticatedAsync(pr, commands, sdk, token)
            };
            await store.SaveAsync(state, token);
        }
        else
        {
            state = await store.LoadAsync(token);
            RunStore.EnsureOutside(store.DirectoryPath, state.SdkRepo, state.SpecRepo);
            state.CheckNextPhase(options.Phase);
            await RepositoryChecks.RootAsync(state.SdkRepo, commands, token);
            await RepositoryChecks.RootAsync(state.SpecRepo, commands, token);
            skill = await File.ReadAllTextAsync(RepositoryChecks.SkillPath(state.SdkRepo), token);
            if (RepositoryChecks.SkillHash(skill) != state.SkillHash)
                throw new InvalidOperationException("The resolution skill changed since planning. Start a fresh plan.");
            var fresh = await GitHub.ReadAuthenticatedAsync(PullRequestId.Parse(state.PrUrl), commands, state.SdkRepo, token);
            if (fresh.Fingerprint != state.Snapshot.Fingerprint)
                throw new InvalidOperationException("The PR head, files, or comments changed. Start a fresh plan.");
        }

        Console.WriteLine($"Local run: {store.DirectoryPath}");
        Console.WriteLine($"Phase: {options.Phase}; PR: {state.PrUrl}");
        if (options.Phase is Phase.Plan or Phase.Apply or Phase.Publish)
            await RepositoryChecks.CheckSdkHeadAsync(state, commands, token);
        if (options.Phase == Phase.Apply)
        {
            await RepositoryChecks.RequireCleanAsync(state.SdkRepo, commands, token);
            await RepositoryChecks.RequireCleanAsync(state.SpecRepo, commands, token);
        }
        var terminal = new Terminal();
        if (options.Phase != Phase.Plan)
        {
            Console.WriteLine(JsonSerializer.Serialize(state.Attempts[^1].Result, JsonSettings.Pretty));
            await terminal.ConfirmAsync(options.Phase switch
            {
                Phase.Apply => "Approve the displayed plan and local implementation? No publication is authorized.",
                Phase.Publish => "Approve the displayed implementation, validation, commits/pushes and draft spec PR?",
                Phase.Review => "Approve only the exact proposed GitHub replies and thread resolutions displayed above?",
                _ => throw new ArgumentOutOfRangeException()
            }, token);
        }
        var result = await new Agent(terminal).RunAsync(options.Phase, state, skill, store, token);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonSettings.Pretty));
        if (result.Status == "ready" && options.Phase == Phase.Publish)
        {
            state.Snapshot = await GitHub.ReadAuthenticatedAsync(PullRequestId.Parse(state.PrUrl), commands, state.SdkRepo, token);
            await store.SaveAsync(state, token);
        }
        Console.WriteLine($"Result saved to {Path.Combine(store.DirectoryPath, "run.json")}");
        return result.Status == "ready" ? 0 : 2;
    }
}
