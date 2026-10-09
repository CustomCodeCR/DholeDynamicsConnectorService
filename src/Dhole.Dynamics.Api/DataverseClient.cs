using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Identity.Client;

namespace Dhole.Dynamics.Api;

public sealed class DataverseClient(HttpClient httpClient, Encryption encryption)
{
    public async Task<DataverseCreateResult> CreateQuoteAsync(
        DynamicsConnection connection, QuoteCreateRequest request, CancellationToken ct)
    {
        var currency = request.TransactionCurrencyId ?? connection.DefaultCurrencyId;
        if (currency is null || currency == Guid.Empty)
            throw new InvalidOperationException("A Dynamics transaction currency GUID is required.");

        var payload = CreatePayload(request, currency.Value);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post,
            $"{connection.DataverseUrl}/api/data/v9.2/quotes");
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        httpRequest.Headers.TryAddWithoutValidation("OData-MaxVersion", "4.0");
        httpRequest.Headers.TryAddWithoutValidation("OData-Version", "4.0");
        httpRequest.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await AcquireTokenAsync(connection, ct));

        using var response = await httpClient.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        var idHeader =
            response.Headers.TryGetValues("OData-EntityId", out var values)
                ? values.FirstOrDefault()
                : response.Headers.Location?.ToString();
        var guidMatch = Regex.Match(idHeader ?? "", @"\(([a-fA-F0-9-]{36})\)");
        if (guidMatch.Success && Guid.TryParse(guidMatch.Groups[1].Value, out var parsed))
            return new DataverseCreateResult(parsed);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > 0)
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("quoteid", out var quoteId)
                && Guid.TryParse(quoteId.GetString(), out parsed))
                return new DataverseCreateResult(parsed);
        }
        throw new InvalidOperationException("Dataverse quote created, but returned no valid GUID. Reconcile before retry.");
    }

    public async Task<string> GetQuoteAsync(
        DynamicsConnection connection, Guid quoteId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{connection.DataverseUrl}/api/data/v9.2/quotes({quoteId:D})" +
            "?$select=quoteid,quotenumber,name,new_pol,new_poe,new_pod,effectivefrom,expireson,new_flete,new_origen,new_destino");
        request.Headers.TryAddWithoutValidation("OData-Version", "4.0");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await AcquireTokenAsync(connection, ct));
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<string> AcquireTokenAsync(DynamicsConnection connection, CancellationToken ct)
    {
        if (!DataverseUrlValidator.TryNormalize(connection.DataverseUrl, out var url))
            throw new InvalidOperationException("Disallowed Dataverse host.");

        var app = ConfidentialClientApplicationBuilder
            .Create(connection.ClientId.ToString("D"))
            .WithAuthority($"https://login.microsoftonline.com/{connection.TenantId:D}")
            .WithClientSecret(encryption.Decrypt(connection))
            .Build();

        var token = await app.AcquireTokenForClient([$"{url}/.default"]).ExecuteAsync(ct);
        return token.AccessToken;
    }

    public static Dictionary<string, object> CreatePayload(QuoteCreateRequest input, Guid currencyId)
    {
        var record = new Dictionary<string, object>
        {
            ["name"] = input.Name,
            ["new_preestado"] = input.PreState ?? 100000003,
            ["new_categoriatarifa"] = input.RateCategory ?? 100000001,
            ["new_tipodecliente"] = input.CustomerType ?? 100000000,
            ["new_tipopropuesta"] = input.ProposalType ?? 100000000,
            ["new_proyecctoespecifico"] = input.SpecificProject ?? 100000004,
            ["new_tipocarga"] = input.CargoType ?? 100000000,
            ["transactioncurrencyid@odata.bind"] = $"/transactioncurrencies({currencyId:D})",
            ["customerid_account@odata.bind"] = $"/accounts({input.CustomerAccountId:D})"
        };
        AddIfNotNull(record, "description", input.Description);
        AddIfNotNull(record, "new_cantidadequipos", input.EquipmentQuantity);
        AddIfNotNull(record, "new_equipo", input.Equipment);
        AddIfNotNull(record, "new_tiempodetransitoaprox", input.TransitTime);
        AddIfNotNull(record, "new_pol", input.Pol);
        AddIfNotNull(record, "new_poe", input.Poe);
        AddIfNotNull(record, "new_pod", input.Pod);
        AddIfNotNull(record, "effectivefrom", input.EffectiveFrom);
        AddIfNotNull(record, "expireson", input.ExpiresOn);
        AddIfNotNull(record, "new_monto1", input.Total);
        AddIfNotNull(record, "new_flete", input.Freight);
        AddIfNotNull(record, "new_origen", input.Origin);
        AddIfNotNull(record, "new_destino", input.Destination);
        // msdyn_invoicesetuptotals is omitted until field writability has been
        // verified against the Dataverse metadata for the target environment.
        return record;
    }

    private static void AddIfNotNull(Dictionary<string, object> record, string key, object? value)
    {
        if (value is not null) record[key] = value;
    }
}
