using System.Text.Json;
namespace Dhole.Dynamics.Contracts;

public sealed record LegacySource(string Repository, string Ref, string Path);
public sealed record LegacyEndpoint(string Id, string Method, string Route,
    string Family, string EntityName, int SourceLine, string Status,
    IReadOnlyList<string> Reasons, string? QueryName, string TemplateName,
    ReadSpec? Query, IReadOnlyList<ReadParamDefinition> ParameterSchema,
    IntegrationTemplate? Template);
public sealed record LegacyBundle(string Format, LegacySource Source,
    IReadOnlyList<LegacyEndpoint> Entries);
public sealed record LegacyMigrationItem(string Id, string Route, string Family,
    string Entity, string Status, string QueryName, string TemplateName,
    IReadOnlyList<string> Reasons, IReadOnlyList<string> RequiredReadFields,
    int? MetadataVersion);
public sealed record LegacyMigrationReport(string Format, string SourceCommit,
    int Total, int Ready, int Staged, int Blocked, IReadOnlyList<LegacyMigrationItem> Items);
public sealed record LegacyImportRequest(bool Confirm);
