# Fase 07 — Plantillas, mappings, vistas y acciones configurables

La Fase 07 ofrece un sistema de **operaciones declarativas y versionadas** sin nuevos controladores por negocio. Continúa requiriendo conexiones, scopes M2M, grants por campo y allowlists de metadatos de las Fases 03–06. No se inició Fase 08 ni se desplegó a producción.

## Catálogo y revisiones

- Migración **007**: `dynamics.integration_templates` (organización, nombre, puntero a versión activa); `dynamics.template_versions` (JSONB **inmutable**, SHA-256, versión y snapshot de metadatos, autor y tiempo); `dynamics.external_references` (IDs Dhole → GUIDs Dataverse aislados por conexión); `dynamics.allowed_actions` (acciones enlazadas explícitamente autorizadas).
- Cada edición/importación crea nueva versión **inactiva**; la versión activa anterior continúa sin cambios. Activación o rollback desplaza únicamente el puntero dentro de una transacción. No hay caché de plantillas, por lo que el cambio es visible en la siguiente petición sin reiniciar.
- Todas las activaciones y ejecuciones vuelven a validar las entidades, columnas y fingerprints vigentes; cambios de schema que revocan columnas bloquean las plantillas anteriores. El historial permanece para auditoría.
- Los nombres `CreateQuote`, `FindShipment`, `UpdateBcf`, `SearchWarehouse` son ejemplos lógicos y se definen como registros (nombre técnico normalizado, p. ej. `create_quote`), no como handlers compilados. Los nombres de entidad/columna reales dependen del snapshot de cada organización.

## DSL de inputs y mapeo

`IntegrationTemplate`: `entityName`, `operation` (**create**, **update**, **upsert**, **delete**, **read**, **find**, **action**), `inputs` (nombre, tipo, obligatorio, default, patrón seguro o límites), `mappings` (campo destino, input/constante y pasos), `select`, `savedQuery` y `actionName` cuando corresponda.

Tipos de input: `string`, `guid`, `integer`, `decimal`, `boolean`, `datetime`. Validación de formato mediante patrones **predefinidos** (`guid`, `email`, `alnum`, `identifier`), min/max, requeridos y defaults. Los únicos pasos permitidos son `trim`, `upper`, `lower`, `date_utc`, `decimal`, `if_empty`, `concat`, `choice_constant`, `unit` y `lookup_ref`. Conversiones de unidades: `lb_to_kg`, `kg_to_lb`, `in_to_m`, `m_to_in`. La cantidad de inputs, campos y pasos por mapping está limitada.

Una asignación `lookup_ref` resuelve `referenceSpace` + `internalId` contra `external_references` **en la misma conexión** y únicamente hacia la tabla destino de una relación Many-to-One registrada. Luego se reutiliza el compilador seguro de `@odata.bind` de Fase 06, que exige permiso de la tabla y campo referidos. No se permite usar un ID como URL ni cambiar de organización desde un parámetro.

Las operaciones `read` y `find` reutilizan la lectura genérica de Fase 05. `find` referencia una **consulta guardada existente y habilitada**; sus parámetros y campos son revalidados, con cursores, scopes, grants y cuotas intactos. Los cambios de la consulta guardada la deshabilitan de nuevo, bloqueando la plantilla.

## Acciones Dataverse, no proxy genérico

Solo se admiten **acciones bound** (enlazadas a una entidad y GUID) con nombre fijo administrativamente aprobado. Su configuración consta de `entityName`, `actionName` y esquema exacto de parámetros. La política nace deshabilitada; editarla la deshabilita otra vez. La ejecutan **exclusivamente** apps M2M con rol `dynamics.actions.invoke`, un grant de operación `action` con campos específicos y marcador `__action_<Nombre>`, y una política habilitada en `allowed_actions`.

Se genera de forma interna la ruta `/api/data/v9.2/<entityset>(<guid>)/Microsoft.Dynamics.CRM.<acción>` sobre el **host HTTPS de la conexión**. No existen opciones para scripts JavaScript, funciones arbitrarias, métodos HTTP personalizables, destinos externos o OData libre. Acciones se envían una sola vez, usando el mismo ledger de idempotencia de Fase 06; fallos de red, timeout o 5xx requieren reconciliación manual.

## Admin API — cookie OIDC y CSRF

- `GET /api/admin/v1/connections/{code}/templates`: nombres, versión activa y historial.
- `GET /api/admin/v1/connections/{code}/templates/{name}`: detalle.
- `POST /api/admin/v1/connections/{code}/templates/{name}/versions`: crea revisión inactiva validada.
- `POST /api/admin/v1/connections/{code}/templates/{name}/versions/{version}/preview`: acepta inputs de ejemplo y construye payload tipado **sin enviar nada a Dataverse**.
- `PUT /api/admin/v1/connections/{code}/templates/{name}/active` con `{ "version": 2 }`: activa una revisión previa o hace rollback. `{ "version": null }` desactiva.
- `GET /api/admin/v1/connections/{code}/templates/{name}/export?version=2`: solo definición y nombre, sin secretos, tokens ni GUIDs de referencias.
- `POST /api/admin/v1/connections/{code}/templates/import`: documento JSON `{ "format":"dhole.template.v1", "name":"create_quote", "definition":{...} }`. Revalida **contra el destino**, crea revisión inactiva; nunca autoactiva.
- `PUT /api/admin/v1/connections/{code}/references`: bind de ID interno Dhole a GUID. Requiere rol **SecurityAdmin**.
- `GET /api/admin/v1/connections/{code}/references/{space}/{entity}/{internalId}`: resolución administrativa.
- `GET/POST /api/admin/v1/connections/{code}/allowed-actions`, `PUT /.../allowed-actions/{entity}/{name}/enabled`: solo **SecurityAdmin**; esquema tipado y aprobación explícita.

La interfaz de Admin `/templates` permite seleccionar organización y campos habilitados, editar definiciones, consultar historial, simular, activar/rollback, importar/exportar y aprobar acciones si se tiene rol SecurityAdmin. El diseñador visual avanzado de todas las operaciones se reserva a Fase 08; el editor JSON actual es totalmente funcional y no requiere editar código ni reiniciar.

## Runtime M2M

`POST /api/v1/templates/{name}/execute`:

```json
{
  "connectionCode": "org",
  "inputs": { "po": " PO123 " },
  "recordId": null,
  "etag": null,
  "keys": null,
  "cursor": null
}
```

Para create/update/upsert/delete/action se exige el header `Idempotency-Key` de Fase 06. Update/delete requieren además recordId, ETag y confirmación de borrado cuando corresponda. Read usa recordId y `select` fijo; find usa SavedQuery y el cursor de Fase 05. Todas las llamadas comprueban conexión, versión activa y metadatos **en cada ejecución**, además de grants, campos, cuotas y roles OAuth.

## Pruebas y límites

Pruebas .NET unitarias: mapping seguro, Choice/unit conversion, lookup_ref por organización, rechazo de ejecución arbitraria, validación de inputs, preview. Integración real PostgreSQL: plantilla nueva sin recompilar, crear v1 inactiva, preview sin enviar, activación, ejecución M2M, v2, rollback a v1, desactivación, referencias aisladas, acción denegada sin grant y ejecutada luego del grant, deshabilitar y bloquear la acción. Se mantienen las pruebas de Fases 02–06, las **7 migraciones**, backups cifrados y scanning de secretos.

**Pendiente para producción:** probar una organización Dataverse y Entra reales en staging, verificar sus action names/metadatos efectivos y Application User, los permisos y datos empresariales, y las políticas de retención. No se usaron credenciales reales para las pruebas automatizadas y no se desplegó en producción.

**Fase 08 no iniciada.**
