using System.Diagnostics;

namespace ReviewSidekick;

internal interface ICommandRunner
{
    Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, CancellationToken cancellationToken, string? input = null);
}

internal sealed class CommandRunner : ICommandRunner
{
    public async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, CancellationToken cancellationToken, string? input = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["GH_PAGER"] = "cat";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (input is not null)
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{executable} failed ({process.ExitCode}): {error.Trim()}");
            return output.TrimEnd();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
