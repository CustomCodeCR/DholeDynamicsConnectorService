using System.Text.Json;

namespace Dhole.Dynamics.Contracts;

/// <summary>Only structured and typed query expressions; no OData or SQL string pass-through.</summary>
public sealed record ReadPredicate(
    string? Field = null, string? Op = null, JsonElement? Value = null,
    string? Parameter = null, string? Join = null, IReadOnlyList<ReadPredicate>? Children = null);

public sealed record ReadSort(string Field, bool Desc = false);
public sealed record ReadExpand(string Navigation, IReadOnlyList<string> Select);

public sealed record ReadSpec(
    IReadOnlyList<string> Select, ReadPredicate? Filter = null,
    IReadOnlyList<ReadSort>? Order = null, IReadOnlyList<ReadExpand>? Expand = null,
    int PageSize = 25, bool Count = false);

public sealed record ReadSearchRequest(string ConnectionCode, ReadSpec? Query = null, string? Cursor = null);
public sealed record ReadRecordResponse(string Entity, JsonElement Item, string? ETag, int MetadataVersion);
public sealed record ReadSearchResponse(string Entity, IReadOnlyList<JsonElement> Items, string? NextCursor,
    long? Count, int Page, int MetadataVersion);

public sealed record ReadParamDefinition(string Name, string Type, bool Required = true);
public sealed record SavedQueryWrite(string Name, string EntityName, ReadSpec Query,
    IReadOnlyList<ReadParamDefinition> Parameters, bool Enabled = false);
public sealed record SavedQuerySummary(Guid Id, string Name, string EntityName, ReadSpec Query,
    IReadOnlyList<ReadParamDefinition> Parameters, bool Enabled, DateTimeOffset UpdatedAt);
public sealed record SavedQueryEnabled(bool Enabled);
public sealed record ExecuteSavedQueryRequest(string ConnectionCode, IReadOnlyDictionary<string, JsonElement> Parameters,
    string? Cursor = null);
