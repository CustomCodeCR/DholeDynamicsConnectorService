# Manual de integración para consumidores externos — Dhole Dynamics Connector

## Aislamiento y autenticación

Use HTTPS del propio conector (`https://<connector-host>`), **no** DholeApiGateway. El consumidor (DholePricingService, DholeSystem u otro) obtiene un **JWT app-only** por OAuth2 client credentials de Entra, con audience del recurso conector, issuer confiable y roles `dynamics.*`. Además la app debe estar registrada y tener grants por conexión + entidad + operación + columnas, de modo deny-by-default. El conector jamás comparte cookies OIDC Admin con M2M.

Cada consumidor debe separar configuración y aplicaciones de staging/producción. Nunca guarde client secret en código/Git; usar un secret manager. No registrar Authorization, cuerpos de tarifas o IDs de correlación junto a datos personales.

## Descubrir y consultar

- `GET /health/live`, `GET /health/ready` prueban proceso y BD (no Dataverse real).
- API de consulta `POST /api/v1/records/{entity}/search` acepta un DSL tipado, **no** OData libre. La respuesta `nextCursor` está ligada al cliente, conexión y metadata, y caduca.
- Para cambios: `GET /api/v1/sync/{entity}/changes?connectionCode=<code>&after=<sequence>&limit=50` requiere grant read del PrimaryId. Retorna sólo `sequence,recordId,changeHash,action,observedAt`. Guarde `nextAfter` cuando complete el procesamiento local; si expira retención requiere full reconcile.
- Respetar cuota 429, `Retry-After` y backoff en **GET**. Nunca reintentar ciegamente POST/PATCH/DELETE cuando la respuesta se pierde.

## Crear Quote de DholeRateId

1. Solicitar a administración que publique plantilla versionada **CreateQuote** y conceda el grant M2M de creación de la entidad quote y campos utilizados.
2. Elegir un `DholeRateId` inmutable por tarifa; la clave debe ser exactamente `dhole-rate-<guid-en-formato-N>`.
3. `POST /api/v1/dhole/quotes` con `{connectionCode,dholeRateId,confirmed:true,inputs:{...}}`, encabezados `Authorization: Bearer ...`, `Idempotency-Key` y `X-Correlation-ID`. Los `inputs` concretos provienen de la definición versionada y validada, no de un catálogo genérico inventado.
4. En 201 conservar `dynamicsQuoteId`; en 200/replay respetar el ID ya existente. En `requires_reconciliation`, consultar el vínculo `GET /api/v1/dhole/quotes/links/{rateId}?connectionCode=<code>`, registrar estado de bloqueo y solicitar verificación administrada; **no crear otra oferta**.
5. En producción las operaciones POST están bloqueadas hasta un cambio aprobado externamente. El conector no habilita una producción por el mero hecho de generar imágenes.

## CRUD, batch y contratos

`POST /api/v1/records/{entity}`, `PATCH /api/v1/records/{entity}/{id}`, `DELETE`, `PUT .../by-key` y `POST /api/v1/records/batch` requieren scopes, grant, metadatos, restricciones por campo e idempotencia. PATCH/DELETE respetan If-Match/ETag. DELETE y acciones potencialmente destructivas exigen aprobación extra en producción.

Las definiciones completas están en `docs/api/phase10-openapi.yaml` y `docs/api/phase11-openapi.yaml`; errores sanitizados ProblemDetails con código estable y correlation ID. SDK tipado reutilizable: `src/Dhole.Dynamics.Client`. Estos contratos **no modifican automáticamente DholePricingService**; la adopción por el consumidor es trabajo explícito y separado.

## Pruebas del consumidor

Staging: obtener token real, verificar rol + grant, buscar cuota/429, crear Quote descartable una sola vez, repetir idempotency key, simular pérdida de respuesta, comprobar conciliación, consultar feed delta y confirmar que un cliente externo NO ve otra conexión. Guardar evidencia sin secretos. Bloquear go-live si cualquiera falla.
