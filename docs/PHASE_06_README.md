# Fase 06 — CRUD genérico, upsert, batch e idempotencia

## Alcance

La API ahora ejecuta operaciones de **escritura** en tablas Dataverse habilitadas por organización, sin crear un controlador específico por tabla. Esta fase no introduce diseñadores de plantillas, transformaciones ejecutables ni sincronizaciones de Fase 07.

- `POST` crea registros de entidades del catálogo, usando valores JSON estructurados y nombres lógicos previamente descubiertos.
- `PATCH` actualiza únicamente los campos indicados, exigiendo un `If-Match` con ETag de Dataverse válido. Un 412 se devuelve como rechazo conocido; no se reintenta automáticamente.
- `DELETE` exige permiso OAuth específico, grant por entidad, política administrativa opt-in, `If-Match`, `confirm: true` y motivo explícito. Si la política establece `minAgeDays > 0`, se hace un GET previo de `createdon`; si no se puede verificar la retención, se bloquea el borrado.
- `PUT` hace upsert mediante una **clave alternativa** presente en el snapshot de Fase 04. Utiliza `PATCH` OData a la clave compuesta, sin crear una ruta arbitraria.
- `POST /records/batch` empaqueta entre 1 y 5 operaciones de una misma conexión en un changeset `$batch`. Verifica los códigos de cada subrespuesta: **HTTP 200 del lote no implica éxito de los elementos**. No hay reenvío implícito de lote y los resultados inciertos quedan para revisión. No se garantizan efectos atómicos fuera de lo informado por Dataverse.

Los atributos soportados por el compilador de escritura incluyen texto, entero, GUID, booleano, Choice, MultiSelect Choice, decimal, moneda, fecha/hora UTC, Lookup, Customer y Owner. `@odata.bind` solo es admisible con nombre de navegación que existe en `ManyToOneRelationships` y un GUID validado; además, la entidad/clave primaria de destino debe estar habilitada para lectura y cubierta por un grant M2M adicional. Se prohíbe inyectar rutas HTTP, nombres de navegación inexistentes y asignación directa a columnas lookup.

## Seguridad

Cada petición M2M requiere Bearer JWT app-only con roles `dynamics.records.create`, `dynamics.records.update`, `dynamics.records.delete` o `dynamics.records.upsert`, **además** del grant habilitado `entity/operation/allowed_fields` en PostgreSQL y de las capacidades exactas `CanCreate/CanUpdate` vigentes en la allowlist de Fase 04. Los borrados se rigen por `dynamics.delete_policies` (deshabilitada por defecto). Admin con rol `dynamics.security.admin` y CSRF gestiona estas políticas y revisa la reconciliación. El administrador no puede utilizar una cookie OIDC para ejecutar rutas M2M.

## API

| Método | Ruta | Entrada |
|---|---|---|
| POST | `/api/v1/records/{entity}` | `{ "connectionCode": "org", "values": {"new_name": "PO-778"} }` |
| PATCH | `/api/v1/records/{entity}/{guid}` | `{ "connectionCode": "org", "values": {"new_name": "Actualizado"} }` |
| DELETE | `/api/v1/records/{entity}/{guid}` | `{ "connectionCode": "org", "confirm": true, "reason": "Baja aprobada por el responsable" }` |
| PUT | `/api/v1/records/{entity}/by-key` | `{ "connectionCode": "org", "keys": {"new_po": "PO-778"}, "values": {"new_status": 1001} }` |
| POST | `/api/v1/records/batch` | `{ "connectionCode": "org", "items": [ { "operation": "create", "entity": "new_shipments", "values": {"new_name": "X"} } ] }` |
| GET | `/api/v1/write-operations/{id}?connectionCode=org` | Solo cliente emisor |

**Headers:** en todas las escrituras `Idempotency-Key` único y estable por intención, de 16–128 caracteres ASCII `[A-Za-z0-9_.-]`. `PATCH` y `DELETE` además requieren `If-Match: W/"1234"` u otra versión ETag concreta. No se acepta `*` como forma de eludir la concurrencia. Los batches con `delete` deben incluir `confirmDelete:true`, `deleteReason` y ETag; no admiten retención mayor a 0 días, que requiere flujo individual.

**Endpoints Admin** protegidos por rol `dynamics.security.admin`, cookies OIDC y CSRF:

- `GET/PUT /api/admin/v1/connections/{code}/delete-policies/{entity}` — `{ "enabled": true, "minAgeDays": 30 }`.
- `GET /api/admin/v1/connections/{code}/write-operations/{id}` — estado y referencia sin payload personal.
- `POST /api/admin/v1/connections/{code}/write-operations/{id}/reconcile` — conciliación manual con motivo, `recordId` confirmado o `confirmedNotApplied` explícito.

## Idempotencia y conciliación

Migración `006_generic_writes.sql` añade `dynamics.write_operations` y `dynamics.delete_policies`, y permite el nuevo operation grant `upsert`. El ledger mantiene una restricción única por **conexión/emisor/cliente/Idempotency-Key** y registra SHA-256 de la intención normalizada, estado, resultado, referencia y auditoría. Las reservas concurrentes compiten por la misma clave en PostgreSQL; solo una puede enviar el comando. Una clave con payload distinto devuelve 409.

Estados: `reserved` antes de enviar, `succeeded` ante éxito confirmado, `rejected` ante 4xx conocido, `ambiguous` si ocurre timeout, fallo de red, error 5xx o respuesta de batch incompleta, y `reconciled` tras revisión manual. Una operación `reserved/ambiguous/reconciled` **jamás** se reenvía automáticamente con la misma clave. En estado ambiguo, el operador revisa el identificador/correlación en Dataverse y decide manualmente si está confirmado o no aplicado. Ninguna conciliación implica reenvío automático.

Dataverse puede responder con 400, 401, 403, 404, 409, 412, 429 y 5xx. Se conservan estados conocidos, se devuelve `Retry-After` cuando el proveedor lo entrega, pero la escritura no se repite automáticamente. Las solicitudes HTTP a Dataverse se limitan al host HTTPS configurado, sin redirecciones ni ruta arbitraria.

## Pruebas y puesta en servicio

`dotnet test DholeDynamicsConnectorService.slnx -c Release`. CI ejecuta pruebas unitarias y los casos de PostgreSQL real de Fases 02–06: concurrencia, timeout posterior a POST, idempotencia, una conexión distinta, 412, Choice inválido, Lookup inválido, ETag, borrado denegado y acceso administrativo; también respaldo/restauración cifrados y escaneo de secretos. Las pruebas de batch usan respuestas HTTP simuladas para verificar status interno.

**No se desplegó a producción y no se usaron secretos reales.** Antes de habilitar una organización real deben completarse las validaciones de identidad OIDC/Entra y roles Dataverse pendientes de Fase 03, probar manualmente CRUD, upsert y batch con su entorno de staging y revisar retención de información. La migración se ejecuta sobre el nombre de base aislado de Fase 02.

**Fase 07 no iniciada.**
