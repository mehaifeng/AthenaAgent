using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

public sealed record GitBranchInfo(string Reference, string Name, string CommitId, string Subject,
    DateTimeOffset? CommittedAt, string Upstream, string TrackingStatus, bool IsRemote,
    bool IsCurrent, bool IsDefault, bool IsMerged, string? WorktreePath);

public sealed record GitBranchSnapshot(string RepositoryRoot, string CurrentName, string? CurrentReference,
    string? BaseReference, bool HasCommits, bool IsDirty, bool OperationInProgress, IReadOnlyList<GitBranchInfo> Branches);

public sealed record GitBranchComparison(string Text, bool IsTruncated);
