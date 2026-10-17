namespace Dhole.Dynamics.Contracts;

public sealed record MetadataCatalog(IReadOnlyList<MetadataTable> Tables);

public sealed record MetadataTable(
    string LogicalName, string EntitySetName, string PrimaryIdAttribute,
    string? PrimaryNameAttribute, bool IsCustom,
    IReadOnlyDictionary<int, string> Labels,
    IReadOnlyList<MetadataColumn> Columns, IReadOnlyList<MetadataRelationship> Relationships);

public sealed record MetadataColumn(
    string LogicalName, string Type, bool CanRead, bool CanCreate, bool CanUpdate,
    bool IsCalculated, string? RequiredLevel, int? Precision,
    string? DefaultValue, IReadOnlyList<string> LookupTargets,
    IReadOnlyDictionary<int, string> Labels, IReadOnlyList<MetadataChoice> Choices,
    int? CurrencyPrecisionSource = null);

public sealed record MetadataChoice(int Value, IReadOnlyDictionary<int, string> Labels);

public sealed record MetadataRelationship(
    string SchemaName, string Kind, string? ReferencedEntity,
    string? ReferencingEntity, string? ReferencedAttribute, string? ReferencingAttribute);

public sealed record MetadataSnapshot(Guid Id, Guid ConnectionId, int Version,
    DateTimeOffset CapturedAt, string Checksum, string Source, MetadataCatalog Catalog);

public sealed record MetadataDiff(
    int? PreviousVersion, int CurrentVersion, bool Unchanged,
    IReadOnlyList<string> AddedEntities, IReadOnlyList<string> RemovedEntities,
    IReadOnlyList<string> ChangedEntities, IReadOnlyList<string> AddedFields,
    IReadOnlyList<string> RemovedFields, IReadOnlyList<string> ChangedFields);

public sealed record MetadataRefreshResult(MetadataSnapshot Snapshot, MetadataDiff Diff);

public sealed record MetadataEntityPolicy(string LogicalName, bool Enabled);
public sealed record MetadataFieldPolicy(string EntityName, string FieldName, bool Enabled,
    bool CanRead, bool CanCreate, bool CanUpdate, string MetadataFingerprint);

public sealed record MetadataPolicies(IReadOnlyList<MetadataEntityPolicy> Entities,
    IReadOnlyList<MetadataFieldPolicy> Fields);

public sealed record MetadataToggle(bool Enabled);
public sealed record MetadataFieldToggle(bool Enabled, bool CanRead, bool CanCreate, bool CanUpdate);
public sealed record MetadataSchedule(bool Enabled, int IntervalMinutes, DateTimeOffset? NextRunAt);
