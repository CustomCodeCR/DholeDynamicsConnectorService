using System.Text.Json;

namespace Dhole.Dynamics.Contracts;

public sealed record TemplateInput(string Name, string Type, bool Required = true,
    JsonElement? Default = null, string? Pattern = null, decimal? Min = null, decimal? Max = null);
public sealed record TemplateStep(string Op, string? Argument = null, string? OtherInput = null,
    JsonElement? Constant = null);
public sealed record TemplateMapping(string Target, string? Source = null,
    IReadOnlyList<TemplateStep>? Steps = null, JsonElement? Constant = null);
public sealed record IntegrationTemplate(string EntityName, string Operation,
    IReadOnlyList<TemplateInput> Inputs, IReadOnlyList<TemplateMapping> Mappings,
    IReadOnlyList<string>? Select = null, string? SavedQuery = null,
    string? ActionName = null);
public sealed record TemplateRevision(int Version, IntegrationTemplate Definition,
    int MetadataVersion, DateTimeOffset CreatedAt, string Sha256);
public sealed record TemplateSummary(Guid Id, string Name, int? ActiveVersion,
    IReadOnlyList<TemplateRevision> History);
public sealed record TemplatePreview(int Version, string Operation, string Entity,
    IReadOnlyDictionary<string,JsonElement> Payload, IReadOnlyList<string> Fields,
    int MetadataVersion, bool WouldSend = false);
public sealed record TemplateInputs(string ConnectionCode, IReadOnlyDictionary<string,JsonElement> Inputs,
    Guid? RecordId = null, string? ETag = null, IReadOnlyDictionary<string,JsonElement>? Keys = null,
    string? Cursor = null, bool ConfirmDelete = false, string? DeleteReason = null);
public sealed record ReferenceWrite(string ReferenceSpace, string InternalId, string EntityName, Guid DataverseId);
public sealed record ReferenceResult(string ReferenceSpace, string InternalId, string EntityName, Guid DataverseId);
public sealed record TemplateExport(string Format, string Name, IntegrationTemplate Definition);
public sealed record TemplateActivation(int? Version);
