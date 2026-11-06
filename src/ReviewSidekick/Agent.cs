using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace ReviewSidekick;

internal sealed class Agent(Terminal terminal)
{
    internal static readonly string[] ReadTools = ["view", "glob", "grep", "ask_user"];
    internal static readonly string[] GeneratorTools =
    [
        "discover_project", "run_code_generation", "build_and_classify", "run_tests",
        "snapshot_generated", "verify_generated_unchanged", "classify_errors"
    ];

    internal static SessionConfig Configure(Phase phase, RunState state)
    {
        var tools = new List<string>(ReadTools);
        Dictionary<string, McpServerConfig>? servers = null;
        if (phase != Phase.Plan)
        {
            tools.AddRange(["powershell", "read_powershell", "stop_powershell"]);
            if (phase is Phase.Apply or Phase.Publish)
            {
                tools.AddRange(["apply_patch", "edit", "create", "skill"]);
                tools.AddRange(GeneratorTools.Select(name => $"mcp:generator-agent-{name}"));
                servers = new()
                {
                    ["generator-agent"] = new McpStdioServerConfig
                    {
                        Command = "dotnet",
                        Args = ["run", "--project", Path.Combine(state.SdkRepo,
                            "sdk", "tools", "Azure.GeneratorAgent", "src", "Azure.GeneratorAgent.csproj"),
                            "--no-launch-profile", "--verbosity", "quiet"],
                        WorkingDirectory = state.SdkRepo,
                        Tools = GeneratorTools,
                        Timeout = 1_800_000
                    }
                };
            }
        }

        return new SessionConfig
        {
            Model = state.Model,
            WorkingDirectory = state.SdkRepo,
            AvailableTools = tools,
            McpServers = servers,
            EnableConfigDiscovery = false,
            EnableFileHooks = false,
            EnableOnDemandInstructionDiscovery = false,
            EnableSessionStore = false,
            EnableSessionTelemetry = false,
            EnableSkills = phase is Phase.Apply or Phase.Publish,
            SkillDirectories = phase is Phase.Apply or Phase.Publish
                ? [Path.Combine(state.SdkRepo, ".github", "skills")] : [],
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = Prompts.Rules
            },
            Hooks = new SessionHooks
            {
                OnPreToolUse = (_, _) => Task.FromResult<PreToolUseHookOutput?>(
                    new() { PermissionDecision = "ask" })
            }
        };
    }

    public async Task<PhaseResult> RunAsync(Phase phase, RunState state, string skill,
        RunStore store, CancellationToken token)
    {
        await using var client = new CopilotClient(new CopilotClientOptions
        {
            WorkingDirectory = state.SdkRepo,
            Connection = RuntimeConnection.ForStdio(state.CopilotPath)
        });
        var config = Configure(phase, state);
        config.OnPermissionRequest = async (request, _) =>
        {
            var details = JsonSerializer.Serialize(request, request.GetType(), JsonSettings.Pretty);
            var answer = await terminal.ReadAsync(
                $"[{phase}] Tool permission request:\n{details}\nType approve for this request only; anything else denies.",
                token);
            // Permission decisions are experimental in the pinned SDK.
#pragma warning disable GHCP001
            return answer == "approve"
                ? PermissionDecision.ApproveOnce()
                : PermissionDecision.Reject("The local user did not approve this operation.");
#pragma warning restore GHCP001
        };
        config.OnUserInputRequest = async (request, _) =>
        {
            var prompt = JsonSerializer.Serialize(new { request.Question, request.Choices }, JsonSettings.Pretty);
            while (true)
            {
                var answer = await terminal.ReadAsync(prompt, token);
                var isChoice = request.Choices?.Contains(answer, StringComparer.Ordinal) == true;
                if (!isChoice && request.AllowFreeform == false)
                {
                    Console.Error.WriteLine("Enter one of the exact listed choices.");
                    continue;
                }
                return new UserInputResponse { Answer = answer, WasFreeform = !isChoice };
            }
        };

        await using var session = await client.CreateSessionAsync(config);
        var attempt = new PhaseAttempt(phase, DateTimeOffset.UtcNow, session.SessionId, null);
        state.Attempts.Add(attempt);
        await store.SaveAsync(state, token);
        using var subscription = session.On<SessionEvent>(e =>
        {
            if (e is ToolExecutionStartEvent tool)
                Console.WriteLine($"[{phase}] {tool.Data.ToolName}");
            else if (e is SessionErrorEvent error)
                Console.Error.WriteLine(JsonSerializer.Serialize(error.Data.Message));
        });

        // The pinned SDK's typed-response helper is experimental; keep usage isolated.
#pragma warning disable GHCP001
        var result = await session.SendAndWaitAsync<PhaseResult>(
            Prompts.Build(phase, state, skill), JsonSettings.Compact,
            timeout: TimeSpan.FromHours(4), cancellationToken: token);
#pragma warning restore GHCP001
        RunState.ValidateResult(result, phase);
        if (phase == Phase.Publish && result.Status == "ready" &&
            PullRequestId.Parse(result.SdkPrUrl!) != PullRequestId.Parse(state.PrUrl))
            throw new InvalidOperationException("Publication reported a different SDK PR; inspect partial work.");
        state.Attempts[^1] = attempt with { Result = result };
        await store.SaveAsync(state, token);
        return result;
    }
}
