using System.Security.Cryptography;
using System.Text;

namespace ReviewSidekick;

internal static class RepositoryChecks
{
    public static string SkillPath(string sdk) => Path.Combine(sdk, ".github", "skills",
        "mgmt-review-comment-resolution", "SKILL.md");

    public static string SkillHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static async Task<string> RootAsync(string path, ICommandRunner commands, CancellationToken token)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            throw new ArgumentException($"Checkout does not exist: {full}");
        var root = await commands.RunAsync("git", ["rev-parse", "--show-toplevel"], full, token);
        if (!string.Equals(Path.GetFullPath(root), full, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException($"Provide the repository root, not a subdirectory: {root}");
        return full;
    }

    public static async Task CheckSdkHeadAsync(RunState state, ICommandRunner commands, CancellationToken token)
    {
        var head = await commands.RunAsync("git", ["rev-parse", "HEAD"], state.SdkRepo, token);
        var branch = await commands.RunAsync("git", ["branch", "--show-current"], state.SdkRepo, token);
        if (head != state.Snapshot.HeadSha || branch != state.Snapshot.HeadBranch)
            throw new InvalidOperationException(
                "The SDK checkout must already be on the SDK PR head branch and commit. No automatic checkout is performed.");
    }

    public static async Task RequireCleanAsync(string repository, ICommandRunner commands, CancellationToken token)
    {
        var status = await commands.RunAsync("git", ["status", "--porcelain"], repository, token);
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException(
                $"Checkout has existing changes: {repository}. Preserve them before applying a fresh plan.\n{status}");
    }
}
