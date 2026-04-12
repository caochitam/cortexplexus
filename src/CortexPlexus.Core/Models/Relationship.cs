namespace CortexPlexus.Core.Models;

public sealed record Relationship(
    string FromFqn,
    string ToFqn,
    RelationshipType Type,
    Dictionary<string, string>? Metadata = null
);

public enum RelationshipType
{
    ContainsProject,
    ContainsNamespace,
    Declares,
    Inherits,
    Implements,
    HasMethod,
    HasProperty,
    HasConstructor,
    Calls,
    Creates,
    UsesType,
    Overrides,
    Throws,
    HandledBy,
    HttpCalls,
    MapsTo,
    DependsOn,
    References,
    BelongsToModule,
    Configures,
    HasSection,
    TestCovers,
    ReadsConfig,
    HasField,
    HasEvent,
    Subscribes,
    Publishes,
    Catches,
    PipelineOrder,
    AcceptsDto,
    ReturnsDto
}
