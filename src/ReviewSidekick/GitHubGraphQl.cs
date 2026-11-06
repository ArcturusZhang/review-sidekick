using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReviewSidekick;

internal sealed class GitHubGraphQl(HttpClient http, Uri endpoint)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSettings.Compact)
    {
        RespectNullableAnnotations = true
    };
    private const string ThreadsQuery = """
        query($owner:String!,$repo:String!,$number:Int!,$cursor:String) {
          repository(owner:$owner,name:$repo) { pullRequest(number:$number) {
            reviewThreads(first:100,after:$cursor) {
              nodes {
                id isResolved isOutdated path line startLine diffSide
                comments(first:100) {
                  nodes { id databaseId body url createdAt updatedAt author { login } }
                  pageInfo { hasNextPage endCursor }
                }
              }
              pageInfo { hasNextPage endCursor }
            }
          }}
        }
        """;
    private const string RepliesQuery = """
        query($id:ID!,$cursor:String!) {
          node(id:$id) { ... on PullRequestReviewThread {
            comments(first:100,after:$cursor) {
              nodes { id databaseId body url createdAt updatedAt author { login } }
              pageInfo { hasNextPage endCursor }
            }
          }}
        }
        """;

    public async Task<ReviewThread[]> ReadThreadsAsync(PullRequestId pr, CancellationToken token)
    {
        var threads = new List<ReviewThread>();
        var seenThreadCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var data = await QueryAsync<ThreadsData>(ThreadsQuery,
                new { owner = pr.Owner, repo = pr.Repository, number = pr.Number, cursor }, token);
            var page = data.Repository?.PullRequest?.ReviewThreads
                ?? throw new InvalidOperationException("GitHub GraphQL did not return review threads.");
            foreach (var node in page.Nodes)
            {
                var comments = node.Comments.Nodes.Select(MapComment).ToList();
                var commentCursor = node.Comments.PageInfo.NextCursor();
                var seenCommentCursors = new HashSet<string>(StringComparer.Ordinal);
                while (commentCursor is not null)
                {
                    if (!seenCommentCursors.Add(commentCursor))
                        throw new InvalidOperationException("GitHub repeated a review-comment pagination cursor.");
                    var replies = await QueryAsync<RepliesData>(RepliesQuery,
                        new { id = node.Id, cursor = commentCursor }, token);
                    var more = replies.Node?.Comments
                        ?? throw new InvalidOperationException("GitHub GraphQL did not return thread replies.");
                    comments.AddRange(more.Nodes.Select(MapComment));
                    commentCursor = more.PageInfo.NextCursor();
                }
                threads.Add(new(node.Id, node.IsResolved, node.IsOutdated, node.Path,
                    node.Line, node.StartLine, node.DiffSide, comments.ToArray()));
            }
            var nextCursor = page.PageInfo.NextCursor();
            if (nextCursor is not null && !seenThreadCursors.Add(nextCursor))
                throw new InvalidOperationException("GitHub repeated a review-thread pagination cursor.");
            cursor = nextCursor;
        } while (cursor is not null);
        return threads.ToArray();
    }

    private static PrComment MapComment(ThreadComment c) =>
        new(c.DatabaseId, c.Id, c.Body, c.Url, c.Author?.Login, c.CreatedAt, c.UpdatedAt);

    private async Task<T> QueryAsync<T>(string query, object variables, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync(endpoint, new { query, variables },
            JsonOptions, token);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<GraphResponse<T>>(JsonOptions, token)
            ?? throw new InvalidOperationException("GitHub GraphQL returned an empty response.");
        if (result.Errors is { Length: > 0 })
            throw new InvalidOperationException(
                $"GitHub GraphQL returned errors: {string.Join("; ", result.Errors.Select(e => e.Message))}");
        return result.Data ?? throw new InvalidOperationException("GitHub GraphQL returned no data.");
    }

    internal sealed record PageInfo(
        [property: JsonRequired] bool HasNextPage,
        [property: JsonRequired] string? EndCursor)
    {
        public string? NextCursor() => !HasNextPage ? null
            : !string.IsNullOrEmpty(EndCursor) ? EndCursor
            : throw new InvalidOperationException("GitHub reported another page without a cursor.");
    }

    private sealed record GraphResponse<T>(T? Data, GraphError[]? Errors);
    private sealed record GraphError(string Message);
    private sealed record Connection<T>(
        [property: JsonRequired] T[] Nodes,
        [property: JsonRequired] PageInfo PageInfo);
    private sealed record ThreadsData(RepositoryNode? Repository);
    private sealed record RepositoryNode(PullRequestNode? PullRequest);
    private sealed record PullRequestNode(Connection<ThreadNode>? ReviewThreads);
    private sealed record RepliesData(ReplyNode? Node);
    private sealed record ReplyNode(Connection<ThreadComment>? Comments);
    private sealed record ThreadNode(
        [property: JsonRequired] string Id,
        [property: JsonRequired] bool IsResolved,
        [property: JsonRequired] bool IsOutdated,
        [property: JsonRequired] string Path,
        int? Line, int? StartLine,
        [property: JsonRequired] string DiffSide,
        [property: JsonRequired] Connection<ThreadComment> Comments);
    private sealed record ThreadComment(
        [property: JsonRequired] string Id, long? DatabaseId, string? Body,
        [property: JsonRequired] string Url,
        [property: JsonRequired] DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
        AuthorNode? Author);
    private sealed record AuthorNode(string Login);
}
