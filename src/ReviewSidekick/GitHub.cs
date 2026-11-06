using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Octokit;
using Octokit.Internal;

namespace ReviewSidekick;

internal sealed record PullRequestId(string Host, string Owner, string Repository, int Number)
{
    public string Repo => $"{Owner}/{Repository}";
    public string Url => $"https://{Host}/{Repo}/pull/{Number}";

    public static PullRequestId Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || Uri.CheckHostName(uri.Host) != UriHostNameType.Dns)
            throw new ArgumentException("Expected an HTTPS GitHub pull request URL without credentials or query.");
        var match = Regex.Match(uri.AbsolutePath,
            @"^/([A-Za-z0-9][A-Za-z0-9-]*)/([A-Za-z0-9_.-]+)/pull/([1-9][0-9]*)/?$");
        if (!match.Success || !int.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, out var number))
            throw new ArgumentException("Expected https://HOST/OWNER/REPOSITORY/pull/NUMBER.");
        return new(uri.Host, match.Groups[1].Value, match.Groups[2].Value, number);
    }

    public Uri RestEndpoint => new(Host == "github.com" ? "https://api.github.com/" : $"https://{Host}/api/v3/");
    public Uri GraphEndpoint => new(Host == "github.com" ? "https://api.github.com/graphql" : $"https://{Host}/api/graphql");
}

internal sealed class GitHub(IGitHubClient rest, GitHubGraphQl graph)
{
    public static async Task<PrSnapshot> ReadAuthenticatedAsync(
        PullRequestId pr, ICommandRunner commands, string workingDirectory, CancellationToken token)
    {
        var credential = (await commands.RunAsync("gh",
            ["auth", "token", "--hostname", pr.Host], workingDirectory, token)).Trim();
        if (string.IsNullOrEmpty(credential))
            throw new InvalidOperationException($"GitHub CLI returned no token for {pr.Host}. Sign in to that host with gh.");

        using var restHandler = new HttpClientHandler();
        var client = CreateRestClient(pr, credential, restHandler, token);
        using var http = CreateGraphClient(credential, new HttpClientHandler { AllowAutoRedirect = false });
        return await new GitHub(client, new GitHubGraphQl(http, pr.GraphEndpoint)).ReadAsync(pr, token);
    }

    internal static IGitHubClient CreateRestClient(PullRequestId pr, string credential,
        HttpMessageHandler handler, CancellationToken token = default) =>
        new GitHubClient(new Connection(new Octokit.ProductHeaderValue("review-sidekick"),
            pr.RestEndpoint, new InMemoryCredentialStore(new Credentials(credential)),
            new HttpClientAdapter(() => new RequestCancellationHandler(token) { InnerHandler = handler }),
            new SimpleJsonSerializer()));

    internal static HttpClient CreateGraphClient(string credential, HttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("review-sidekick");
        return http;
    }

    public async Task<PrSnapshot> ReadAsync(PullRequestId pr, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pull = await rest.PullRequest.Get(pr.Owner, pr.Repository, pr.Number).WaitAsync(token);
        if (pull.State.Value != ItemState.Open || pull.Merged)
            throw new InvalidOperationException("The SDK PR must be open and unmerged.");

        var metadata = new PrMetadata(pull.HtmlUrl, pull.Title, pull.Body, pull.Draft,
            pull.Head.Sha, pull.Head.Ref,
            pull.Head.Repository?.FullName ?? throw new InvalidOperationException("The SDK PR head repository is unavailable."),
            pull.Base.Ref, pull.Base.Repository.FullName, pull.MaintainerCanModify);

        var options = new ApiOptions { PageSize = 100 };
        var files = await rest.PullRequest.Files(pr.Owner, pr.Repository, pr.Number, options).WaitAsync(token);
        if (files.Count != pull.ChangedFiles)
            throw new InvalidOperationException("GitHub did not return every changed file; refusing an incomplete PR snapshot.");
        var comments = await rest.Issue.Comment.GetAllForIssue(pr.Owner, pr.Repository, pr.Number, options).WaitAsync(token);
        var reviews = await rest.PullRequest.Review.GetAll(pr.Owner, pr.Repository, pr.Number, options).WaitAsync(token);
        var inline = await rest.PullRequest.ReviewComment.GetAll(pr.Owner, pr.Repository, pr.Number, options).WaitAsync(token);
        var threads = await graph.ReadThreadsAsync(pr, token);
        return new(metadata,
            files.Select(f => new ChangedFile(f.Sha, f.FileName, f.PreviousFileName, f.Status,
                f.Additions, f.Deletions, f.Changes, f.Patch, f.BlobUrl, f.RawUrl, f.ContentsUrl)).ToArray(),
            comments.Select(c => new PrComment(c.Id, c.NodeId, c.Body, c.HtmlUrl, c.User?.Login,
                c.CreatedAt, c.UpdatedAt)).ToArray(),
            reviews.Select(r => new PrReview(r.Id, r.NodeId, r.Body, r.HtmlUrl, r.User?.Login,
                r.State.StringValue, r.CommitId,
                r.State.Value == PullRequestReviewState.Pending ? null : r.SubmittedAt)).ToArray(),
            inline.Select(c => new InlineComment(
                new PrComment(c.Id, c.NodeId, c.Body, c.HtmlUrl, c.User?.Login, c.CreatedAt, c.UpdatedAt),
                c.Path, c.Position, c.OriginalPosition, c.DiffHunk, c.CommitId, c.OriginalCommitId,
                c.InReplyToId, c.PullRequestReviewId)).ToArray(),
            threads);
    }

    // Octokit's high-level REST methods do not accept a cancellation token.
    private sealed class RequestCancellationHandler(CancellationToken runToken) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(runToken, token);
            return await base.SendAsync(request, linked.Token);
        }
    }
}
