using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReviewSidekick;

internal sealed record PrMetadata(
    string Url, string Title, string? Body, bool Draft, string HeadSha, string HeadBranch,
    string HeadRepo, string BaseBranch, string BaseRepo, bool? MaintainerCanModify);

internal sealed record ChangedFile(
    string Sha, string FileName, string? PreviousFileName, string Status,
    int Additions, int Deletions, int Changes, string? Patch,
    string BlobUrl, string RawUrl, string ContentsUrl);

internal sealed record PrComment(
    long? Id, string NodeId, string? Body, string Url, string? Author,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

internal sealed record PrReview(
    long Id, string NodeId, string? Body, string Url, string? Author,
    string State, string CommitId, DateTimeOffset? SubmittedAt);

internal sealed record InlineComment(
    PrComment Comment, string Path, int? Position, int? OriginalPosition, string? DiffHunk,
    string CommitId, string OriginalCommitId, long? InReplyToId, long? PullRequestReviewId);

internal sealed record ReviewThread(
    string Id, bool IsResolved, bool IsOutdated, string Path, int? Line, int? StartLine,
    string DiffSide, PrComment[] Comments);

internal sealed record PrSnapshot(
    PrMetadata Metadata, ChangedFile[] Files, PrComment[] ConversationComments,
    PrReview[] Reviews, InlineComment[] InlineComments, ReviewThread[] Threads)
{
    [JsonIgnore]
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, JsonSettings.Compact))));

    [JsonIgnore]
    public string HeadSha => Metadata.HeadSha;
    [JsonIgnore]
    public string HeadBranch => Metadata.HeadBranch;
}
