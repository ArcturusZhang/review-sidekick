namespace ReviewSidekick;

internal sealed class Terminal
{
    private readonly SemaphoreSlim inputLock = new(1, 1);

    public async Task<string> ReadAsync(string question, CancellationToken token)
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("Interactive input is required; redirected input is not supported.");
        await inputLock.WaitAsync(token);
        try
        {
            Console.WriteLine(question);
            return await Console.In.ReadLineAsync(token)
                ?? throw new InvalidOperationException("Input closed; approval was not granted.");
        }
        finally
        {
            inputLock.Release();
        }
    }

    public async Task ConfirmAsync(string question, CancellationToken token)
    {
        if (await ReadAsync($"{question}\nType approve to continue; anything else cancels.", token) != "approve")
            throw new OperationCanceledException("Not approved.");
    }
}
