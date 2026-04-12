namespace CortexPlexus.Core.Models;

public sealed record IndexingJob(
    string FilePath,
    Guid RepoId,
    ChangeType ChangeType
);

public enum ChangeType { Created, Modified, Deleted }

public sealed record RepositoryInfo(
    Guid Id,
    string Name,
    string Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastIndexed
);
