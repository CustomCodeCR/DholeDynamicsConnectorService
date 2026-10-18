# Fase 07 — Diseñador de plantillas, mappings y acciones configurables

**Estado técnico:** implementación de Fase 07 integrada en API .NET y panel Vue. La ejecución externa con Microsoft Dataverse real requiere credenciales y autorización en staging. No incluye funciones de Fase 08.

## Funcionalidades

- `dynamics.integration_templates` y `dynamics.template_versions`: nombre y organización, revisiones inmutables con checksum, versión del snapshot, auditoría y puntero de versión activa. Activar `null` deshabilita la operación; seleccionar una versión anterior permite rollback sin recompilar ni reiniciar la API.
- `IntegrationTemplate`: operación declarada (`create`, `update`, `upsert`, `delete`, `read`, `find`, `action`), entidad lógica, inputs tipados, defaults, límites y patrones, asignación origen→campo, transformación declarativa, select y referencia a consulta guardada.
- Transformaciones permitidas: `trim`, `upper`, `lower`, `date_utc`, `decimal`, `if_empty`, `concat`, `choice_constant`, `unit` (`lb_to_kg`, `kg_to_lb`, `in_to_m`, `m_to_in`) y `lookup_ref`. Sin scripts, expresiones arbitrarias, reflexión ni destinos HTTP definidos por clientes.
- `external_references` ampliada para relacionar identificador interno en un namespace con GUID de Dataverse **por conexión y entidad**, sin exponer credenciales ni inferir el destino de un input no fiable.
- Vistas parametrizadas: `find` reutiliza consultas guardadas y autorizadas de Fase 05; `read` aplica proyección explícita. Las escrituras utilizan Fase 06 y conservan las cuotas, los roles, la idempotencia, el ETag y los controles de eliminación. Las acciones vinculadas requieren allowlist independiente, OAuth y grant M2M.
- Import/export JSON de definiciones (formato `dhole.template.v1`) sin credenciales; cada importación crea nueva revisión **no activada** y revalida su entidad, campos y permisos frente a los metadatos locales.
- Simulación preview (`wouldSend=false`): valida inputs, mappings, relación y payload esperado sin enviar cambios a Dataverse. La activación valida nuevamente el snapshot. En cada ejecución se vuelven a verificar políticas del momento.
- Panel Vue `/templates`: selector de conexión, creación, revisión de JSON, historial, preview, activación, desactivación, rollback, import/export y políticas de acciones. El panel usa Admin OIDC y CSRF; ejecutar requiere JWT M2M, no la cookie Admin.

## Endpoints principales

Admin (cookie OIDC + rol Admin + protección CSRF):
- `GET /api/admin/v1/connections/{code}/templates`
- `GET /api/admin/v1/connections/{code}/templates/{name}`
- `POST /api/admin/v1/connections/{code}/templates/{name}/versions`
- `POST /api/admin/v1/connections/{code}/templates/{name}/versions/{version}/preview`
- `PUT /api/admin/v1/connections/{code}/templates/{name}/active` con `{"version":2}` (o `null` para desactivar)
- `GET /api/admin/v1/connections/{code}/templates/{name}/export?version=2`
- `POST /api/admin/v1/connections/{code}/templates/import`
- `GET /api/admin/v1/connections/{code}/references/{space}/{entity}/{internalId}`

SecurityAdmin:
- `PUT /api/admin/v1/connections/{code}/references`
- `GET/POST /api/admin/v1/connections/{code}/allowed-actions`
- `PUT /api/admin/v1/connections/{code}/allowed-actions/{entity}/{name}/enabled`

M2M (Bearer app-only):
- `POST /api/v1/templates/{name}/execute` con `{"connectionCode":"org","inputs":{"po":"123"}}`. Para escrituras o acciones enviar `Idempotency-Key`, y ETag/GUID/confirmación cuando lo exige Fase 06.

## Ejemplo: nueva operación sin editar C#

Con la tabla `new_shipment` y su campo `new_name` ya habilitados para create en Fase 04, y un grant M2M create de Fase 03:

```json
{
  "entityName": "new_shipment",
  "operation": "create",
  "inputs": [{"name":"po","type":"string","required":true}],
  "mappings": [{"target":"new_name","source":"po","steps":[{"op":"upper"}]}]
}
```

Guardar como una nueva revisión de la plantilla `CreateShipment` desde Admin; ejecutar **preview** con `{"po":"abc"}` debe generar `new_name: "ABC"` y **no** emitir HTTP a Dataverse. Activar la versión 1, llamar a `/api/v1/templates/CreateShipment/execute` desde un cliente autorizado y comprobar la creación. Crear versión 2 con transformación `lower`, activarla y comprobar comportamiento nuevo sin reiniciar. Volver a versión 1 para rollback.

Otros nombres como `CreateQuote`, `FindShipment`, `UpdateBcf` o `SearchWarehouse` no necesitan controladores dedicados: requieren que los nombres lógicos, operaciones y campos estén presentes y autorizados en la organización correspondiente.

## Pruebas y limitaciones

La migración 007 añade el versionado y las políticas por organización y amplía `external_references`. El job CI `docker-smoke` aplica las siete migraciones y ejecuta `Phase07TemplateTests` con PostgreSQL: se verifica una operación nueva introducida por configuración, estado inicial inactivo, preview sin llamada HTTP, activación, segunda versión, rollback, aislamiento por conexión, referencias y autorización independiente de acciones. Los unit tests cubren transforms finitos, conversiones, lookups y rechazo de scripts/rutas externas. Continúan las pruebas de regresión Fases 02–06 y respaldo/restauración cifrados.

**No confundir CI con una prueba E2E externa:** aún deben validarse credenciales Entra, privilegios efectivos de Dataverse, existencia real de las acciones enlazadas y aprobación operativa antes de cualquier despliegue de producción. Tampoco se inicia Fase 08.
