using System.Net;
using System.Text;
using System.Text.Json;
using Octokit;

namespace ReviewSidekick.Tests;

public sealed class GitHubTests
{
    [Theory]
    [InlineData("github.com", "https://api.github.com/", "https://api.github.com/graphql")]
    [InlineData("github.example.com", "https://github.example.com/api/v3/", "https://github.example.com/api/graphql")]
    public void SelectsHostSpecificEndpoints(string host, string rest, string graph)
    {
        var pr = new PullRequestId(host, "Azure", "sdk", 42);
        Assert.Equal(new Uri(rest), pr.RestEndpoint);
        Assert.Equal(new Uri(graph), pr.GraphEndpoint);
    }

    [Fact]
    public async Task OctokitCollectsAllRestPagesAndGraphQlCollectsThreadsAndReplies()
    {
        using var fixture = new GitHubFixture();
        var snapshot = await fixture.ReadAsync();
        Assert.Equal("feature", snapshot.HeadBranch);
        Assert.Equal("user/sdk", snapshot.Metadata.HeadRepo);
        Assert.Equal(2, snapshot.Files.Length);
        Assert.Equal("src/Thing.cs", snapshot.Files[0].FileName);
        Assert.Equal("@@ patch", snapshot.Files[0].Patch);
        Assert.Equal(2, snapshot.ConversationComments.Length);
        Assert.Equal("discussion page 2", snapshot.ConversationComments[1].Body);
        Assert.Equal("reviewer", snapshot.ConversationComments[0].Author);
        Assert.Equal(2, snapshot.Reviews.Length);
        Assert.Equal("COMMENTED", snapshot.Reviews[0].State);
        Assert.Equal(2, snapshot.InlineComments.Length);
        Assert.Equal(201, snapshot.InlineComments[1].InReplyToId);
        Assert.Equal("@@ diff", snapshot.InlineComments[0].DiffHunk);
        Assert.Equal(2, snapshot.Threads.Length);
        Assert.Equal(42, snapshot.Threads[0].Line);
        Assert.Equal(2, snapshot.Threads[0].Comments.Length);
        Assert.Equal(202, snapshot.Threads[0].Comments[1].Id);
        Assert.Equal("PRRC_202", snapshot.Threads[0].Comments[1].NodeId);
        Assert.Null(snapshot.Threads[0].Comments[1].Author);
        Assert.True(snapshot.Threads[1].IsResolved);
        Assert.True(snapshot.Threads[1].IsOutdated);
        Assert.Equal(3, fixture.GraphCalls);
        Assert.Equal(9, fixture.Requests.Count(r => r.Method == HttpMethod.Get));
        Assert.All(fixture.Requests, r => Assert.Equal("api.github.com", r.Uri.Host));
        Assert.All(fixture.Requests, r => Assert.Contains("test-credential", r.Authorization));
        Assert.DoesNotContain("test-credential", JsonSerializer.Serialize(snapshot, JsonSettings.Pretty));
        var restored = JsonSerializer.Deserialize<PrSnapshot>(JsonSerializer.Serialize(snapshot, JsonSettings.Compact), JsonSettings.Compact)!;
        Assert.Equal(snapshot.Fingerprint, restored.Fingerprint);
    }

    [Fact]
    public async Task EnterpriseRequestsAndPaginationStayOnTheSelectedHost()
    {
        using var fixture = new GitHubFixture { Host = "github.example.com" };
        var snapshot = await fixture.ReadAsync();
        Assert.Equal(2, snapshot.ConversationComments.Length);
        Assert.All(fixture.Requests, r =>
        {
            Assert.Equal("github.example.com", r.Uri.Host);
            Assert.StartsWith(r.Method == HttpMethod.Get ? "/api/v3/" : "/api/graphql", r.Uri.AbsolutePath);
        });
    }

    [Fact]
    public async Task FingerprintTracksHeadReviewsInlineFeedbackAndThreadState()
    {
        using var fixture = new GitHubFixture();
        var snapshot = await fixture.ReadAsync();
        PrSnapshot[] changes =
        [
            snapshot with { Metadata = snapshot.Metadata with { HeadSha = "different" } },
            snapshot with { Reviews = [snapshot.Reviews[0] with { Body = "edited review" }, snapshot.Reviews[1]] },
            snapshot with { InlineComments = [snapshot.InlineComments[0] with
                { Comment = snapshot.InlineComments[0].Comment with { Body = "edited inline feedback" } }, snapshot.InlineComments[1]] },
            snapshot with { Threads = [snapshot.Threads[0] with { IsResolved = true }, snapshot.Threads[1]] },
            snapshot with { Threads = [snapshot.Threads[0] with
                { Comments = [snapshot.Threads[0].Comments[0], snapshot.Threads[0].Comments[1] with { Body = "edited reply" }] }, snapshot.Threads[1]] }
        ];
        Assert.All(changes, changed => Assert.NotEqual(snapshot.Fingerprint, changed.Fingerprint));
    }

    [Theory]
    [InlineData("""{"errors":[{"message":"not authorized"}]}""")]
    [InlineData("""{"data":null}""")]
    [InlineData("""{"data":{"repository":null}}""")]
    [InlineData("""{"data":{"repository":{"pullRequest":null}}}""")]
    public async Task GraphQlErrorsAndMissingDataFailInsteadOfReturningEmptyFeedback(string graphResponse)
    {
        using var fixture = new GitHubFixture { GraphOverride = graphResponse };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
    }

    [Fact]
    public async Task MissingGraphQlPaginationMetadataFails()
    {
        using var fixture = new GitHubFixture
        {
            GraphOverride = """{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[]}}}}}"""
        };
        await Assert.ThrowsAsync<JsonException>(() => fixture.ReadAsync());
    }

    [Fact]
    public async Task NullGraphQlNodeListFails()
    {
        using var fixture = new GitHubFixture
        {
            GraphOverride = """
                {"data":{"repository":{"pullRequest":{"reviewThreads":{
                "nodes":null,"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}}
                """
        };
        await Assert.ThrowsAsync<JsonException>(() => fixture.ReadAsync());
    }

    [Fact]
    public void MissingGraphQlCursorFailsExplicitly() =>
        Assert.Throws<InvalidOperationException>(() => new GitHubGraphQl.PageInfo(true, null).NextCursor());

    [Fact]
    public async Task RepeatedGraphQlCursorFails()
    {
        using var fixture = new GitHubFixture
        {
            GraphOverride = """
                {"data":{"repository":{"pullRequest":{"reviewThreads":{
                "nodes":[],"pageInfo":{"hasNextPage":true,"endCursor":"same-cursor"}}}}}}
                """
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        Assert.Equal(2, fixture.GraphCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GraphQlHttpFailuresAreSurfaced(HttpStatusCode status)
    {
        using var fixture = new GitHubFixture { GraphStatus = status };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.ReadAsync());
    }

    [Fact]
    public async Task RestAuthenticationFailuresAreSurfaced()
    {
        using var fixture = new GitHubFixture { RestStatus = HttpStatusCode.Unauthorized };
        await Assert.ThrowsAsync<AuthorizationException>(() => fixture.ReadAsync());
    }

    [Theory]
    [InlineData("closed", false)]
    [InlineData("open", true)]
    public async Task ClosedAndMergedPrsAreRejected(string state, bool merged)
    {
        using var fixture = new GitHubFixture { State = state, Merged = merged };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task IncompleteFileListIsRejected()
    {
        using var fixture = new GitHubFixture { ChangedFiles = 3001 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        Assert.Equal(0, fixture.GraphCalls);
    }

    [Fact]
    public async Task PreCancelledCollectionDoesNotSendRequests()
    {
        using var fixture = new GitHubFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ReadAsync(cancellation.Token));
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationReachesInFlightHttpRequests(bool graph)
    {
        using var fixture = new GitHubFixture { BlockGraph = graph, BlockRest = !graph };
        using var cancellation = new CancellationTokenSource();
        var read = fixture.ReadAsync(cancellation.Token);
        await fixture.BlockedRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await fixture.BlockedRequestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task EmptyCliTokenIsRejectedWithoutAnyApiFallback()
    {
        var commands = new TokenCommand();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHub.ReadAuthenticatedAsync(new("github.example.com", "Azure", "sdk", 42),
                commands, ".", CancellationToken.None));
        Assert.Contains("returned no token", error.Message);
        Assert.Equal(["auth", "token", "--hostname", "github.example.com"], commands.Arguments);
    }

    private sealed class TokenCommand : ICommandRunner
    {
        public IReadOnlyList<string>? Arguments { get; private set; }
        public Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
            string workingDirectory, CancellationToken cancellationToken, string? input = null)
        {
            Assert.Equal("gh", executable);
            Arguments = arguments;
            return Task.FromResult("");
        }
    }
}

internal sealed class GitHubFixture : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Uri, string Authorization)> Requests { get; } = [];
    public int GraphCalls { get; private set; }
    public string? GraphOverride { get; init; }
    public HttpStatusCode GraphStatus { get; init; } = HttpStatusCode.OK;
    public HttpStatusCode RestStatus { get; init; } = HttpStatusCode.OK;
    public int ChangedFiles { get; init; } = 2;
    public string State { get; init; } = "open";
    public bool Merged { get; init; }
    public string Host { get; init; } = "github.com";
    public bool BlockGraph { get; init; }
    public bool BlockRest { get; init; }
    public TaskCompletionSource BlockedRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource BlockedRequestCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<PrSnapshot> ReadAsync(CancellationToken token = default)
    {
        var pr = new PullRequestId(Host, "Azure", "sdk", 42);
        var rest = GitHub.CreateRestClient(pr, "test-credential", this, token);
        using var graph = GitHub.CreateGraphClient("test-credential", this);
        return await new GitHub(rest, new GitHubGraphQl(graph, pr.GraphEndpoint)).ReadAsync(pr, token);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        Requests.Add((request.Method, uri, request.Headers.Authorization?.ToString() ?? ""));
        if (request.Method == HttpMethod.Post && BlockGraph || request.Method == HttpMethod.Get && BlockRest)
        {
            BlockedRequestStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                if (token.IsCancellationRequested)
                    BlockedRequestCancelled.TrySetResult();
            }
        }
        string body;
        var status = RestStatus;
        var isList = false;
        var page = uri.Query.Contains("page=2", StringComparison.Ordinal) ? 2 : 1;
        if (request.Method == HttpMethod.Post)
        {
            GraphCalls++;
            status = GraphStatus;
            using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var variables = input.RootElement.GetProperty("variables");
            body = GraphOverride ?? (variables.TryGetProperty("id", out _) ? """
                {"data":{"node":{"comments":{
                "nodes":[{"id":"PRRC_202","databaseId":202,"body":"reply","url":"https://github.com/Azure/sdk/pull/42#discussion_r202",
                "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z","author":null}],
                "pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                """ : variables.GetProperty("cursor").ValueKind == JsonValueKind.Null ? """
                {"data":{"repository":{"pullRequest":{"reviewThreads":{
                "nodes":[{"id":"thread1","isResolved":false,"isOutdated":false,"path":"src/Thing.cs",
                "line":42,"startLine":null,"diffSide":"RIGHT",
                "comments":{"nodes":[{"id":"PRRC_201","databaseId":201,"body":"rename","url":"https://github.com/Azure/sdk/pull/42#discussion_r201",
                "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z","author":{"login":"reviewer"}}],
                "pageInfo":{"hasNextPage":true,"endCursor":"reply-page"}}}],
                "pageInfo":{"hasNextPage":true,"endCursor":"thread-page"}}}}}}
                """ : """
                {"data":{"repository":{"pullRequest":{"reviewThreads":{
                "nodes":[{"id":"thread2","isResolved":true,"isOutdated":true,"path":"src/Thing.cs",
                "line":null,"startLine":null,"diffSide":"RIGHT",
                "comments":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}],
                "pageInfo":{"hasNextPage":false,"endCursor":null}}}}}}
                """);
        }
        else if (uri.AbsolutePath.EndsWith("/repos/Azure/sdk/pulls/42", StringComparison.Ordinal))
        {
            body = JsonSerializer.Serialize(new
            {
                state = State,
                merged = Merged,
                merged_at = Merged ? "2026-01-01T00:00:00Z" : null,
                html_url = "https://github.com/Azure/sdk/pull/42",
                title = "SDK PR",
                body = "description",
                draft = true,
                changed_files = ChangedFiles,
                head = new { sha = "abc", @ref = "feature", repo = new { full_name = "user/sdk" } },
                @base = new { @ref = "main", repo = new { full_name = "Azure/sdk" } },
                maintainer_can_modify = true
            });
        }
        else
        {
            isList = true;
            var author = new { login = "reviewer" };
            const string date = "2026-01-01T00:00:00Z";
            object item = uri.AbsolutePath.Split('/')[^1] switch
            {
                "files" => new
                {
                    sha = "file-sha",
                    filename = "src/Thing.cs",
                    status = "modified",
                    additions = 1,
                    deletions = 0,
                    changes = 1,
                    patch = "@@ patch",
                    blob_url = "https://github.com/blob",
                    raw_url = "https://github.com/raw",
                    contents_url = "https://api.github.com/contents"
                },
                "reviews" => new
                {
                    id = 100 + page,
                    node_id = $"PRR_{100 + page}",
                    body = $"review page {page}",
                    html_url = "https://github.com/review",
                    user = author,
                    state = "COMMENTED",
                    commit_id = "abc",
                    submitted_at = date
                },
                _ when uri.AbsolutePath.Contains("/issues/", StringComparison.Ordinal) => new
                {
                    id = page,
                    node_id = $"IC_{page}",
                    body = $"discussion page {page}",
                    html_url = "https://github.com/discussion",
                    user = author,
                    created_at = date,
                    updated_at = date
                },
                _ => new
                {
                    id = 200 + page,
                    node_id = $"PRRC_{200 + page}",
                    body = $"inline page {page}",
                    html_url = "https://github.com/inline",
                    user = author,
                    created_at = date,
                    updated_at = date,
                    path = "src/Thing.cs",
                    position = 3,
                    original_position = 3,
                    diff_hunk = "@@ diff",
                    commit_id = "abc",
                    original_commit_id = "previous",
                    in_reply_to_id = page == 2 ? (int?)201 : null,
                    pull_request_review_id = 101
                }
            };
            body = JsonSerializer.Serialize(new[] { item });
        }

        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            RequestMessage = request
        };
        if (isList && page == 1)
            response.Headers.TryAddWithoutValidation("Link",
                $"<{uri.GetLeftPart(UriPartial.Path)}?page=2&per_page=100>; rel=\"next\"");
        return response;
    }
}
