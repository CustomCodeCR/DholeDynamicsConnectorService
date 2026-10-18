# Fase 11 — Cola durable, sincronización incremental y resiliencia

**Estado:** implementación integrada en API y panel Vue. Activación de workers **opt-in** mediante `Jobs__Enabled=true`; en Compose está **desactivado por defecto**. No hay despliegue de producción, conexiones a DholeSystem/Redis ni tareas automáticas activadas.

## Topología y aislamiento

La API .NET y `DurableJobWorker` usan exclusivamente la BD PostgreSQL `dhole_dynamics_connector`, esquema `dynamics`; no comparten DB, OAuth ni caché con Dhole. La migración **010** crea `job_queue`, `sync_config`, `sync_checkpoints`, `sync_changes`, `job_alerts`, `job_circuits` y `operational_retention`. El worker dentro de la API corre solo si se habilita la opción de entorno. Puede haber varias instancias usando `FOR UPDATE SKIP LOCKED`; las versiones de lease y los commits transaccionales bloquean la escritura de checkpoints por un worker obsoleto.

Las tareas soportadas son **`delta_sync`**, **`metadata_refresh`**, **`legacy_preview`**, **`legacy_import`** (staging de definiciones *desactivadas*, con confirmación) y **`reconcile_scan`** (*solo* detecta operaciones ambiguas, sin resolverlas ni volverlas a ejecutar).

No hay job de creación/actualización/borrado reintentable: una escritura cuyo resultado sea incierto permanece en `write_operations` y en su flujo de conciliación supervisada de Fases 06/10. La cola nunca llama POST/PATCH/DELETE de Dataverse. `legacy_import` modifica exclusivamente definiciones locales idempotentes según Fase 09.

## Sincronización y checkpoints

1. SecurityAdmin elige `connectionCode` y una entidad habilitada con campo ID de lectura aprobado; para `watermark` o `fallbackToWatermark` también debe aprobar `modifiedon`.
2. Configura `mode=delta`, `fallbackToWatermark=false|true`, `intervalMinutes=15..10080`, `enabled=true` vía UI **Jobs** o API. Por seguridad ningún registro se sincroniza sin esa configuración.
3. En `delta`, el worker utiliza un GET Dataverse v9.2 con `Prefer: odata.track-changes`, proyección exclusiva del ID y máximo 100 por página. Acepta únicamente el endpoint de la entidad en la **misma URL HTTPS CRM previamente autorizada**; `nextLink/deltaLink` del proveedor se validan antes de almacenarse y reutilizarse. Nunca se toma una URL suministrada por el consumidor como destino HTTP.
4. El modo `watermark` usa `modifiedon ge <checkpoint>` y orden estable por `modifiedon` e ID. Solo entra de manera explícita o como fallback a `400/501` al *inicio* de delta con `fallbackToWatermark=true`. El fallback no observa eliminaciones: para escenarios que requieran tombstones, es obligatorio resolver la compatibilidad de delta.
5. Una página se persiste junto con su nuevo checkpoint en **una transacción**, controlada por lease y versión. El manifiesto `sync_changes` guarda solo ID, acción `upsert/delete` y SHA-256 de versión, sin columnas de cliente, nombres, carga OData ni tokens.
6. Un reinicio entre GET y COMMIT reobtiene la página segura; un reinicio después del COMMIT continúa desde `next_link`. La clave única de eventos impide duplicar la misma observación. La configuración de igual modo conserva el cursor, mientras que cambiar el modo lo reinicia de forma explícita. Los jobs completados dejan los resultados disponibles para la consulta Admin.

**Límite:** el manifiesto es una bitácora técnica de cambios, no un almacén completo ni un proceso de replicación hacia Dhole. No se realizan efectos secundarios comerciales. El primer inicio de `watermark` consulta por defecto hasta siete días atrás; ajuste o ejecute un backfill explícito antes de depender de histórico mayor.

## Retry, circuito y dead-letter

- Los trabajos leen un máximo de 100 observaciones por solicitud; el cliente HTTP tiene timeout de 50 segundos, sin redirects ni cookies.
- 429, 5xx, timeouts y errores de red/token temporal se clasifican como transitorios. Se vuelve a intentar **solo** GET o tareas idempotentes, con `Retry-After`, backoff exponencial con jitter (15–3600 s) y un máximo de cinco intentos. 400/403 y metadatos desautorizados se envían a dead-letter, nunca por fuerza bruta.
- Después de tres fallos transitorios sucesivos por conexión, se abre un circuito cinco minutos para metadata/delta. El registro y la alerta quedan en DB.
- Los leases duran 120 s y se renuevan cada 25 s. Un lease vencido vuelve a reclamarse únicamente tras caducar; si agotó los intentos, se mueve a dead-letter y genera alerta. Un worker con lease antiguo no puede modificar checkpoint ni estado.
- SecurityAdmin puede **reprocesar solo dead-letter** con motivo (mínimo 15 caracteres) y confirmación CSRF. Jamás se reprocesan escrituras ambiguas.
- La reconciliación periódica solo crea la alerta `write_reconciliation_required` para escrituras reservadas o ambiguas con más de cinco minutos, sin enviar transacciones nuevas a Dataverse.

## Contratos de operación (cookie Admin OIDC)

Lecturas Admin:

```text
GET /api/admin/v1/operations/jobs?connectionCode=staging&limit=50
GET /api/admin/v1/operations/jobs/{id}?code=staging
GET /api/admin/v1/operations/sync/staging
GET /api/admin/v1/operations/sync/staging/checkpoints
GET /api/admin/v1/operations/stats
GET /api/admin/v1/operations/alerts?connectionCode=staging
GET /api/admin/v1/operations/retention
GET /api/admin/v1/operations/audit/export?connectionCode=staging
GET /health/jobs
```

Mutaciones **SecurityAdmin** con cookie de sesión OIDC + `X-CSRF-TOKEN`:

```http
PUT /api/admin/v1/operations/sync/staging
Content-Type: application/json

{"entityName":"quote","enabled":true,"mode":"delta","fallbackToWatermark":false,"intervalMinutes":60}
```

```http
POST /api/admin/v1/operations/jobs/staging
Content-Type: application/json

{"kind":"delta_sync","entityName":"quote","dedupeKey":"sync-2026-10-18-quote-0001"}
```

```http
POST /api/admin/v1/operations/jobs/staging/11111111-1111-4111-8111-111111111111/replay
Content-Type: application/json

{"confirm":true,"reason":"Verified source connectivity and the failed read was safe to replay."}
```

Otras mutaciones: `PUT /api/admin/v1/operations/retention` (`jobDays`, `observationDays`, `changeDays`: 7–365); `POST /api/admin/v1/operations/alerts/{id}/ack`. Para `legacy_import` se exige `confirm:true` y rol SecurityAdmin. Los CSV no incluyen actor IDs ni payloads, conservan solo IDs estructurales, acción y fecha; exportación limitada a mil entradas por solicitud.

## Observabilidad y privacidad

`GET /health/jobs` distingue el worker habilitado del deshabilitado y muestra conteos seguros de cola, dead-letter, leases expirados y alertas. `GET /api/admin/v1/operations/stats` agrega `pending/running/completed/dead`, cambios en 24 h y alarmas. `/jobs` en Vue expone configuración, checkpoints (sin tokens), reproceso supervisado, estado y alertas. No se guardan respuestas JSON de Dataverse ni cuerpos HTTP.

Retención por defecto: 30 días para jobs **completados**, observaciones HTTP y manifiesto de cambios; `dead`, `pending`, `running`, alertas sin reconocer, operaciones del ledger idempotente y `audit_events` **no se eliminan** automáticamente. El ajuste de retención no modifica snapshots cifrados, credenciales ni respaldos.

Se usa **polling/consulta del estado**, no webhooks push; no se añade un sistema de callbacks abierto y no hay endpoints configurables ni riesgo adicional SSRF de destinos salientes. Si algún consumidor necesita push en el futuro, requerirá aprobación, firma por mensaje, pinning de destinos e infraestructura propia; **no está habilitado en esta fase**.

## Verificaciones y límites operativos

CI debe aprobar build/test en .NET, tests unitarios de URLs, 429 y fallos transitorios, y pruebas reales PostgreSQL de leases, checkpoints, deduplicación, reanudación y reproceso. Docker smoke aplica exactamente diez migraciones; restaura backup cifrado. Un test con respuestas simuladas verifica el contrato delta, **no prueba disponibilidad de Microsoft Dataverse de una organización real**. No habilite sincronizaciones hasta aprobar metadata y derechos de acceso en staging.

Rollback sin pérdida: primero `Jobs__Enabled=false` y reiniciar API. La cola y checkpoints quedan intactos y el esquema 010 permanece; no revertir la migración SQL ni descartar tareas vivas. Rehabilitar después de corregir condición, y usar la pantalla de dead-letter para trabajo agotado. Para incidentes y pasos ejecutables consulte [runbook](runbooks/PHASE_11_RECOVERY.md).
