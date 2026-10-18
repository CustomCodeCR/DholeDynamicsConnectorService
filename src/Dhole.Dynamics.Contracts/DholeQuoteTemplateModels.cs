using System.Text.Json;

namespace Dhole.Dynamics.Contracts;

/// <summary>Explicitly confirmed request; fields are a versioned CreateQuote template's input names.</summary>
public sealed record DholeQuoteTemplateRequest(string ConnectionCode, Guid DholeRateId,
    bool Confirmed, IReadOnlyDictionary<string, JsonElement> Inputs);
public sealed record DholeQuoteTemplateResult(Guid DholeRateId, Guid? DynamicsQuoteId,
    string Status, bool AlreadyExisted, Guid? OperationId, string? QuoteNumber = null);
