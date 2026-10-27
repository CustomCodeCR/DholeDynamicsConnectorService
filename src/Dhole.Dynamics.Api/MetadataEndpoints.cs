using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

namespace Dhole.Dynamics.Api;

public sealed record MetadataChoicesRequest(IReadOnlyList<string> Fields);
public sealed record CreateQuoteAlternateKeyRequest(string Field, bool Confirm);

public static class MetadataEndpoints
{
    public static IEndpointRouteBuilder MapMetadataEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/v1/connections/{code}/metadata")
            .WithGroupName("admin-v1").RequireAuthorization("Admin")
            .RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        group.MapGet("", GetCurrent);
        group.MapGet("/diff", GetDiff);
        group.MapGet("/policies", GetPolicies);
        group.MapPost("/refresh", Refresh);
        group.MapPost("/entities/{entity}/choices", ReadChoices)
            .RequireAuthorization("SecurityAdmin");
        group.MapPost("/entities/{entity}/alternate-keys/refresh", RefreshAlternateKeys)
            .RequireAuthorization("SecurityAdmin");
        group.MapPost("/entities/{entity}/alternate-keys", CreateQuoteAlternateKey)
            .RequireAuthorization("SecurityAdmin");
        group.MapPut("/entities/{entity}/enabled", SetEntity);
        group.MapPut("/entities/{entity}/fields/{field}/policy", SetField);
        group.MapPut("/entities/{entity}/fields/bulk-enable", BulkEnableFields)
            .RequireAuthorization("SecurityAdmin");
        group.MapPost("/quote-integration/prepare", PrepareQuoteIntegration)
            .RequireAuthorization("SecurityAdmin");
        group.MapGet("/schedule", GetSchedule);
        group.MapPut("/schedule", SetSchedule);
        return app;
    }

    private static async Task<IResult> GetCurrent(string code, IConnectionRepository connections,
        MetadataRepository metadata, HttpContext http, CancellationToken ct)
    {
        var conn = await connections.FindAsync(code, ct);
        if (conn is null) return NotFound(http);
        var current = await metadata.LatestAsync(conn.Id, ct);
        return current is null ? ApiProblems.Create(http, 404, "metadata_not_discovered",
            "No metadata snapshot. Refresh from Dataverse first.") : Results.Ok(current);
    }

    private static async Task<IResult> GetDiff(string code, IConnectionRepository connections,
        MetadataRepository metadata, HttpContext http, CancellationToken ct)
    {
        var conn = await connections.FindAsync(code, ct);
        if (conn is null) return NotFound(http);
        var diff = await metadata.CompareLatestAsync(conn.Id, ct);
        return diff is null ? ApiProblems.Create(http, 404, "metadata_not_discovered",
            "No metadata snapshot.") : Results.Ok(diff);
    }

    private static async Task<IResult> GetPolicies(string code, IConnectionRepository connections,
        MetadataRepository metadata, HttpContext http, CancellationToken ct)
    {
        var conn = await connections.FindAsync(code, ct);
        return conn is null ? NotFound(http) : Results.Ok(await metadata.PoliciesAsync(conn.Id, ct));
    }

    private static async Task<IResult> Refresh(string code, MetadataRefreshService service,
        System.Security.Claims.ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        try
        {
            var current = await service.RefreshAsync(code, "manual", ScopeSecurity.ActorId(actor), ct);
            return current is null ? NotFound(http) : Results.Ok(current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or InvalidOperationException or System.Text.Json.JsonException
                or Microsoft.Identity.Client.MsalException)
        {
            return ApiProblems.Create(http, 502, "metadata_refresh_failed",
                "Dataverse metadata refresh failed; previous snapshot was preserved.");
        }
    }

    private static async Task<IResult> ReadChoices(string code, string entity,
        MetadataChoicesRequest body, WriteAccessService writer,
        DataverseMetadataDiscovery discovery, HttpContext http, CancellationToken ct)
    {
        if (!DataverseMetadataDiscovery.SafeName(entity) ||
            body.Fields is not { Count: > 0 and <= 30 } ||
            body.Fields.Any(f => !DataverseMetadataDiscovery.SafeName(f)))
            return ApiProblems.Create(http, 400, "invalid_choice_request",
                "Select 1 to 30 valid fields.");
        try
        {
            var policy = await writer.ResolveAsync(code, entity, ct);
            var result = new Dictionary<string, IReadOnlyList<CrudSchemaOption>>(StringComparer.Ordinal);
            foreach (var name in body.Fields.Distinct(StringComparer.Ordinal))
            {
                var field = policy.Table.Columns.FirstOrDefault(c => c.LogicalName == name);
                var approval = policy.Permissions.Fields.FirstOrDefault(p =>
                    p.EntityName == entity && p.FieldName == name);
                if (field is null || field.Type is not ("Picklist" or "State" or "Status") ||
                    approval is null || !approval.Enabled ||
                    approval.MetadataFingerprint != MetadataRepository.Fingerprint(field))
                    return ApiProblems.Create(http, 403, "choice_field_denied",
                        "Field is not approved in the Dataverse catalog.");
                var options = await discovery.DiscoverChoicesAsync(
                    policy.Connection, entity, name, field.Type, ct);
                result[name] = options.Select(o => new CrudSchemaOption(
                    CrudTemplateSchemaBuilder.Label(o.Labels, o.Value.ToString()), o.Value)).ToArray();
            }
            return Results.Ok(new { options = result });
        }
        catch (ReadRejected ex)
        {
            return ApiProblems.Create(http, ex.Status, ex.Code,
                "Dataverse metadata access was denied.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or InvalidOperationException or System.Text.Json.JsonException
                or Microsoft.Identity.Client.MsalException)
        {
            return ApiProblems.Create(http, 502, "choice_metadata_unavailable",
                "Dataverse choice metadata is temporarily unavailable.");
        }
    }

    public static bool CanCreateQuoteAlternateKey(MetadataTable table, string field)
    {
        var column = table.Columns.FirstOrDefault(c => c.LogicalName == field);
        return table.LogicalName == "quote" &&
            // One approved permanent identity only. Do not make other Quote
            // business fields unique through this administrative shortcut.
            field == "new_idtraasignado" &&
            DataverseMetadataDiscovery.SafeName(field) &&
            column is { Type: "String", CanRead: true, CanCreate: true,
                CanUpdate: true, IsCalculated: false };
    }

    // Does not modify a quote, reuse a key name blindly, or create a column.
    // A metadata key is a Dataverse UNIQUE INDEX; user confirmation is required.
    private static async Task<IResult> CreateQuoteAlternateKey(
        string code, string entity, CreateQuoteAlternateKeyRequest request,
        WriteAccessService writer, DataverseMetadataDiscovery discovery,
        HttpContext http, CancellationToken ct)
    {
        if (!request.Confirm || code != "principal" || entity != "quote" ||
            string.IsNullOrWhiteSpace(request.Field))
            return ApiProblems.Create(http,400,"alternate_key_confirmation_required",
                "A SecurityAdmin must confirm a single existing Quote column.");
        try
        {
            var policy = await writer.ResolveAsync(code, entity, ct);
            if (!CanCreateQuoteAlternateKey(policy.Table,request.Field))
                throw new ReadRejected(409,"alternate_key_field_incompatible");
            var alreadyActive = (policy.Table.AlternateKeys??[]).Any(k=>
                k.Attributes.Count==1 && k.Attributes[0]==request.Field);
            if (alreadyActive)
                throw new ReadRejected(409,"alternate_key_already_active");
            var result = await discovery.CreateQuoteAlternateKeyAsync(
                policy.Connection, request.Field, ct);
            return Results.Ok(new
            {
                status=result.Status.ToLowerInvariant(),
                field=request.Field,
                keyName=result.LogicalName,
                message=result.Status switch
                {
                    "Active"=>"La clave ya está activa. Actualizá claves para seleccionarla.",
                    "Failed"=>"La clave existe pero su índice falló. Revisá duplicados en Dataverse y reactivala; no se generó otra.",
                    "InProgress"=>"Dataverse todavía está construyendo el índice único. No se creó una segunda clave.",
                    _=>"Creación solicitada. Dataverse construye la clave sin modificar ninguna oferta; consultá su estado."
                }
            });
        }
        catch (ReadRejected ex)
        {
            return ApiProblems.Create(http,ex.Status,ex.Code,
                "No se pudo crear la clave alternativa en la columna seleccionada.");
        }
        catch (DataverseKeyCreationRejected ex)
        {
            // A rejected Dataverse write is not an origin-gateway outage.
            // Give a stable sanitized code with the upstream status and
            // preserve the existing quote data, field mappings and key grants.
            var (httpStatus, failureCode, reason) = ex.RemoteStatus switch
            {
                400 when ex.Reason=="display_name" => (422, "dataverse_key_display_name_rejected",
                    "Dataverse rechazó el nombre visible de la clave. Verificá la configuración de idiomas del entorno."),
                400 when ex.Reason=="secured_field" => (422, "dataverse_key_secured_field",
                    "Dataverse no permite crear una clave sobre una columna con seguridad de campo."),
                400 when ex.Reason=="key_size" => (422, "dataverse_key_size_exceeded",
                    "El tamaño definido para la columna supera el límite de índices de Dataverse (900 bytes)."),
                400 when ex.Reason=="existing_or_duplicate" => (409, "dataverse_key_existing_or_duplicate",
                    "Dataverse reporta una clave duplicada o existente. Actualizá las claves; no se intentó crear otra."),
                400 when ex.Reason=="schema_name" => (422, "dataverse_key_schema_name_invalid",
                    "Dataverse rechazó el nombre del índice o el prefijo de publicador."),
                400 when ex.Reason=="attribute" => (422, "dataverse_key_attribute_invalid",
                    "Dataverse rechazó la columna new_idtraasignado como componente de la clave."),
                400 => (422, "dataverse_key_metadata_invalid",
                    "Dataverse rechazó la definición de la clave. Revisá el tipo, permisos de metadata, el nombre y el tamaño de la columna."),
                401 or 403 => (403, "dataverse_key_metadata_permission_denied",
                    "Dataverse rechazó los permisos para administrar claves de Oferta."),
                409 or 412 => (409, "dataverse_key_metadata_conflict",
                    "Dataverse detectó una clave existente o conflicto. Actualizá el estado antes de reintentar."),
                429 => (503, "dataverse_key_metadata_throttled",
                    "Dataverse está limitando solicitudes. Consultá el estado antes de enviar otra."),
                _ => (424, "dataverse_key_metadata_rejected",
                    "Dataverse no aceptó la definición de clave. Consultá el estado antes de reintentar.")
            };
            return ApiProblems.Create(http,httpStatus,failureCode,
                reason + " Estado HTTP de Dataverse: " + ex.RemoteStatus + ".");
        }
        catch (TimeoutException)
        {
            // Dataverse may still complete the index request after the HTTP
            // timeout. Never tell the caller that the key definitely failed.
            return ApiProblems.Create(http,504,"alternate_key_confirmation_timeout",
                "Dataverse tardó demasiado en confirmar la solicitud. Verificá el estado de la clave antes de reintentar; no se eliminó ninguna columna.");
        }
        catch (Exception ex) when (ex is HttpRequestException or
            TaskCanceledException or InvalidOperationException or
            Microsoft.Identity.Client.MsalException)
        {
            return ApiProblems.Create(http,502,"alternate_key_creation_failed",
                "Dataverse no aceptó crear la clave. Revisá privilegios, datos duplicados e índices pendientes. " +
                "No se modificó ningún registro de la oferta.");
        }
    }

    private static async Task<IResult> RefreshAlternateKeys(
        string code, string entity, MetadataRefreshService service,
        System.Security.Claims.ClaimsPrincipal actor,
        HttpContext http, CancellationToken ct)
    {
        try
        {
            var report = await service.RefreshAlternateKeysAsync(
                code, entity, ScopeSecurity.ActorId(actor), ct);
            return Results.Ok(new {
                alternateKeys=report.AlternateKeys,
                count=report.AlternateKeys.Count,
                keyStatuses=report.KeyStatuses,
                message=report.AlternateKeys.Count>0
                  ? "Claves activas encontradas. No se cambió ningún campo ni relación."
                  : report.KeyStatuses.Count>0
                    ? "Hay claves registradas en Dataverse, pero ninguna está activa. Consultá keyStatuses."
                    : "Dataverse no tiene todavía claves alternativas para esta tabla."
            });
        }
        catch(ReadRejected ex)
        {
            return ApiProblems.Create(http,ex.Status,ex.Code,
                "No se pudieron actualizar las claves alternativas.");
        }
        catch(Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or InvalidOperationException or System.Text.Json.JsonException
                or Microsoft.Identity.Client.MsalException)
        {
            return ApiProblems.Create(http,502,"alternate_key_refresh_failed",
                "No se pudo consultar la metadata de claves en Dataverse; configuración anterior conservada.");
        }
    }

    private static async Task<IResult> SetEntity(string code, string entity, MetadataToggle body,
        IConnectionRepository connections, MetadataRepository metadata,
        System.Security.Claims.ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        if (!DataverseMetadataDiscovery.SafeName(entity))
            return ApiProblems.Create(http, 400, "invalid_entity", "Invalid entity logical name.");
        var conn = await connections.FindAsync(code, ct);
        if (conn is null) return NotFound(http);
        return await metadata.SetEntityAsync(conn.Id, entity, body.Enabled, ScopeSecurity.ActorId(actor), ct)
            ? Results.NoContent()
            : ApiProblems.Create(http, 409, "entity_not_in_snapshot", "Entity is absent from latest snapshot.");
    }

    private static async Task<IResult> SetField(string code, string entity, string field,
        MetadataFieldToggle body, IConnectionRepository connections, MetadataRepository metadata,
        System.Security.Claims.ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        if (!DataverseMetadataDiscovery.SafeName(entity) || !MetadataFieldName(field))
            return ApiProblems.Create(http, 400, "invalid_field", "Invalid metadata name.");
        var conn = await connections.FindAsync(code, ct);
        if (conn is null) return NotFound(http);
        return await metadata.SetFieldAsync(conn.Id, entity, field, body, ScopeSecurity.ActorId(actor), ct)
            ? Results.NoContent()
            : ApiProblems.Create(http, 409, "metadata_write_denied",
                "Enable the entity first and select only capabilities approved by Dataverse metadata.");
    }

    private static async Task<IResult> BulkEnableFields(string code, string entity,
        MetadataBulkFieldToggle body, IConnectionRepository connections,
        MetadataRepository metadata, System.Security.Claims.ClaimsPrincipal actor,
        HttpContext http, CancellationToken ct)
    {
        if (!DataverseMetadataDiscovery.SafeName(entity))
            return ApiProblems.Create(http, 400, "invalid_entity", "Invalid entity logical name.");
        if (!body.Confirm || body.SnapshotVersion < 1)
            return ApiProblems.Create(http, 400, "invalid_bulk_confirmation",
                "A valid catalog version and explicit confirmation are required.");
        var conn = await connections.FindAsync(code, ct);
        if (conn is null) return NotFound(http);
        var result = await metadata.EnableFieldsBulkAsync(
            conn.Id, entity, body, ScopeSecurity.ActorId(actor), ct);
        return Results.Ok(result);
    }

    public sealed record PrepareQuoteIntegrationRequest(bool Confirm);

    /// <summary>
    /// Explicit, minimal authorization needed for creating the standard Quote
    /// total via quotedetail. It does not create records, grant client credentials,
    /// or enable unrelated metadata fields.
    /// </summary>
    private static async Task<IResult> PrepareQuoteIntegration(
        string code, PrepareQuoteIntegrationRequest request,
        IConnectionRepository connections, MetadataRepository metadata,
        MetadataRefreshService refresh, DataverseMetadataDiscovery discovery,
        System.Security.Claims.ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        if (!request.Confirm || code != "principal")
            return ApiProblems.Create(http, 400, "quote_setup_confirmation_required",
                "An explicit administrator confirmation for principal is required.");
        var connection = await connections.FindAsync(code, ct);
        if (connection is null) return NotFound(http);
        var actorId = ScopeSecurity.ActorId(actor);
        try
        {
            var snapshot = await metadata.LatestAsync(connection.Id, ct);
            if (snapshot is null || new[] { "quote", "quotedetail" }.Any(name =>
                snapshot.Catalog.Tables.All(t => t.LogicalName != name)))
            {
                await refresh.RefreshAsync(code, "manual", actorId, ct);
                snapshot = await metadata.LatestAsync(connection.Id, ct);
            }
            if (snapshot is null || new[] { "quote", "quotedetail" }.Any(name =>
                snapshot.Catalog.Tables.All(t => t.LogicalName != name)))
                return ApiProblems.Create(http, 409, "quote_table_not_in_dataverse_catalog",
                    "Dataverse must expose both quote and quotedetail tables to this connection.");

            foreach (var entity in new[] { "quote", "quotedetail" })
            {
                var table = snapshot.Catalog.Tables.Single(t => t.LogicalName == entity);
                if (table.Columns.Count == 0)
                {
                    await refresh.RefreshTableAsync(code, entity, actorId, ct);
                    snapshot = (await metadata.LatestAsync(connection.Id, ct))!;
                    table = snapshot.Catalog.Tables.Single(t => t.LogicalName == entity);
                }
                // Fetch only actual ManyToOne navigation properties; never
                // infer the customer, custom IDTRA, or quoteid bind names.
                var relations = await discovery.DiscoverLookupRelationshipsAsync(
                    connection, entity, ct);
                if (entity == "quotedetail" && !relations.Any(r =>
                    r.ReferencingAttribute == "quoteid" && r.ReferencedEntity == "quote"))
                    return ApiProblems.Create(http, 409, "quote_detail_relationship_missing",
                        "The quoteid navigation property was not exposed by Dataverse.");
                await metadata.SaveTableAsync(connection.Id,
                    table with { Relationships = relations }, actorId, ct);
                snapshot = (await metadata.LatestAsync(connection.Id, ct))!;
            }

            // Only targets of the two explicit business lookup fields are
            // prepared. No wildcard grants or arbitrary table privileges.
            var relatedTargetNames = snapshot.Catalog.Tables.Single(t => t.LogicalName == "quote")
                .Relationships.Where(r =>
                    r.ReferencingAttribute is "customerid" or "new_idtra1" &&
                    r.ReferencedEntity is not null)
                .Select(r => r.ReferencedEntity!)
                .Distinct(StringComparer.Ordinal).Take(8).ToArray();
            var missingTargets = new List<string>();
            foreach (var targetName in relatedTargetNames)
            {
                snapshot = (await metadata.LatestAsync(connection.Id, ct))!;
                var related = snapshot.Catalog.Tables.FirstOrDefault(t => t.LogicalName == targetName);
                if (related is null)
                {
                    missingTargets.Add(targetName);
                    continue;
                }
                if (related.Columns.Count == 0)
                {
                    await refresh.RefreshTableAsync(code, targetName, actorId, ct);
                    snapshot = (await metadata.LatestAsync(connection.Id, ct))!;
                    related = snapshot.Catalog.Tables.Single(t => t.LogicalName == targetName);
                }
                var identity = related.Columns.FirstOrDefault(c =>
                    c.LogicalName == related.PrimaryIdAttribute);
                if (identity is not { CanRead: true })
                {
                    missingTargets.Add(targetName);
                    continue;
                }
                if (!await metadata.SetEntityAsync(connection.Id, targetName, true, actorId, ct))
                {
                    missingTargets.Add(targetName);
                    continue;
                }
                // Only identity plus legitimate exact-name lookup attributes.
                // An Incident IDTRA may be identified by its ticket number
                // rather than title. Both must be explicitly readable.
                var readableNames = new[] { related.PrimaryIdAttribute, related.PrimaryNameAttribute,
                        targetName == "incident" ? "ticketnumber" : null }
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal);
                foreach (var fieldName in readableNames)
                {
                    var attr = related.Columns.FirstOrDefault(c => c.LogicalName == fieldName);
                    if (attr is not { CanRead: true } ||
                        !await metadata.SetFieldAsync(connection.Id, targetName, fieldName!,
                            new MetadataFieldToggle(true, true, false, false), actorId, ct))
                        missingTargets.Add(targetName + "." + fieldName);
                }
            }
            snapshot = (await metadata.LatestAsync(connection.Id, ct))!;
            var quote = snapshot.Catalog.Tables.Single(t => t.LogicalName == "quote");
            var detail = snapshot.Catalog.Tables.Single(t => t.LogicalName == "quotedetail");
            string[] required =
            [
                "quoteid", "isproductoverridden", "ispriceoverridden",
                "productdescription", "quotedetailname", "quantity", "priceperunit"
            ];
            var unavailable = required.Where(name =>
                detail.Columns.FirstOrDefault(f => f.LogicalName == name) is not { CanCreate: true, IsCalculated: false })
                .ToArray();
            if (unavailable.Length > 0)
                return Results.Conflict(new
                {
                    code = "quote_detail_fields_not_writable",
                    fields = unavailable,
                    message = "Dataverse does not allow direct create for these quotedetail fields."
                });
            var primaryId = quote.Columns.FirstOrDefault(f =>
                f.LogicalName == quote.PrimaryIdAttribute);
            if (primaryId is not { CanRead: true })
                return ApiProblems.Create(http, 409, "quote_primary_id_not_readable",
                    "Dataverse must expose the primary Quote identifier for read.");

            foreach (var name in new[] { "quote", "quotedetail" })
                if (!await metadata.SetEntityAsync(connection.Id, name, true, actorId, ct))
                    return ApiProblems.Create(http, 409, "quote_entity_enable_failed",
                        "Could not enable a quote integration table.");
            if (!await metadata.SetFieldAsync(connection.Id, "quote",
                quote.PrimaryIdAttribute, new MetadataFieldToggle(true, true, false, false),
                actorId, ct))
                return ApiProblems.Create(http, 409, "quote_primary_id_enable_failed",
                    "Could not enable reading the quote ID.");
            foreach (var name in required)
            {
                var column = detail.Columns.Single(f => f.LogicalName == name);
                if (!await metadata.SetFieldAsync(connection.Id, "quotedetail", name,
                    new MetadataFieldToggle(true, column.CanRead, true, false),
                    actorId, ct))
                    return ApiProblems.Create(http, 409, "quote_detail_field_enable_failed",
                        "Could not authorize the required quotedetail field " + name + ".");
            }
            return Results.Ok(new
            {
                prepared = true, tables = new[] { "quote", "quotedetail" },
                enabledDetailFields = required,
                quoteIdRead = true,
                lookupTargetTablesNotReady = missingTargets,
                lookupRelationships = quote.Relationships.Where(r =>
                    r.ReferencingAttribute is "customerid" or "new_idtra1")
                    .Select(r => new
                    {
                        field = r.ReferencingAttribute,
                        targetTable = r.ReferencedEntity,
                        navigation = r.ReferencingNavigation
                    }),
                message = "Metadata and field policies prepared. The named API key still needs an explicit quote-total grant."
            });
        }
        catch (ReadRejected ex)
        {
            return ApiProblems.Create(http, ex.Status, ex.Code,
                "The connector could not prepare quote metadata and permissions.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or InvalidOperationException or System.Text.Json.JsonException
            or Microsoft.Identity.Client.MsalException)
        {
            return ApiProblems.Create(http, 502, "quote_setup_metadata_unavailable",
                "Unable to discover or authorize the quote tables. No Dataverse records were written.");
        }
    }

    private static async Task<IResult> GetSchedule(string code, IConnectionRepository connections,
        MetadataRepository metadata, HttpContext http, CancellationToken ct)
    {
        var conn = await connections.FindAsync(code, ct);
        return conn is null ? NotFound(http) : Results.Ok(await metadata.GetScheduleAsync(conn.Id, ct));
    }

    private static async Task<IResult> SetSchedule(string code, MetadataSchedule schedule,
        IConnectionRepository connections, MetadataRepository metadata,
        System.Security.Claims.ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        if (schedule.IntervalMinutes is < 60 or > 10080)
            return ApiProblems.Create(http, 400, "invalid_schedule", "Refresh interval must be 60 to 10080 minutes.");
        var conn = await connections.FindAsync(code, ct);
        if (conn is null) return NotFound(http);
        await metadata.SetScheduleAsync(conn.Id, schedule, ScopeSecurity.ActorId(actor), ct);
        return Results.NoContent();
    }

    private static bool MetadataFieldName(string field) =>
        DataverseMetadataDiscovery.SafeName(field) ||
        field is { Length: > 7 and <= 128 } && field[0] == '_' &&
        field.EndsWith("_value", StringComparison.Ordinal) &&
        DataverseMetadataDiscovery.SafeName(field[1..^6]);

    private static IResult NotFound(HttpContext http) =>
        ApiProblems.Create(http, 404, "connection_not_found", "Connection not found.");
}
