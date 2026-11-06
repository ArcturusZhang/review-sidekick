namespace ReviewSidekick;

internal enum Phase { Plan, Apply, Publish, Review }

internal sealed record Options(
    Phase Phase, string? Pr, string? SdkRepo, string? SpecRepo,
    string? RunDirectory, string? Model, string? CopilotPath)
{
    public const string Help = """
        Review Sidekick - local .NET / Copilot SDK runner

        review-sidekick plan --sdk-pr URL --sdk-repo PATH --spec-repo PATH [--model ID]
             [--copilot-path PATH] [--run-dir PATH]
        review-sidekick apply --run-dir PATH
        review-sidekick publish --run-dir PATH
        review-sidekick review --run-dir PATH

        plan is the default when no command is supplied.
        Each phase runs separately. Mutating phases require interactive approval.
        Plan only reads files and GitHub data; it does not start GeneratorAgent.
        State defaults to ~/.review-sidekick/runs/<unique-id>.
        """;

    public static Options Parse(string[] args)
    {
        var index = 0;
        var phase = Phase.Plan;
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            phase = args[0] switch
            {
                "plan" => Phase.Plan,
                "apply" => Phase.Apply,
                "publish" => Phase.Publish,
                "review" => Phase.Review,
                _ => throw new ArgumentException($"Unknown command: {args[0]}")
            };
            index++;
        }

        var allowed = new HashSet<string>
        {
            "--sdk-pr", "--sdk-repo", "--spec-repo", "--run-dir", "--model", "--copilot-path"
        };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (; index < args.Length; index += 2)
        {
            var key = args[index];
            if (!allowed.Contains(key) || index + 1 == args.Length ||
                string.IsNullOrWhiteSpace(args[index + 1]) ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(key, args[index + 1]))
            {
                throw new ArgumentException($"Unknown, duplicate, or missing-value option: {key}");
            }
        }

        string? Get(string key) => values.GetValueOrDefault(key);
        if (phase == Phase.Plan)
        {
            foreach (var key in new[] { "--sdk-pr", "--sdk-repo", "--spec-repo" })
                if (!values.ContainsKey(key))
                    throw new ArgumentException($"Required option: {key}");
        }
        else if (values.Count != 1 || !values.ContainsKey("--run-dir"))
        {
            throw new ArgumentException("Continuation commands accept only --run-dir PATH.");
        }

        return new(phase, Get("--sdk-pr"), Get("--sdk-repo"), Get("--spec-repo"),
            Get("--run-dir"), Get("--model"), Get("--copilot-path"));
    }
}
