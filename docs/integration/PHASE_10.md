# Fase 10 — Contrato externo DholeSystem / Pricing (staging solamente)

## Límite y estado

Fase 10 no despliega, no habilita escrituras de producción y **no instala credenciales**. DholeDynamicsConnectorService sigue independiente: no utiliza DholeApiGateway, DholeAuth, ni las bases de Pricing. DholeSystem es hoy un repositorio principalmente de arquitectura/orquestación; el propietario de la oferta formal es DholePricingService. La habilitación real requiere un registro Entra App M2M y una organización Dataverse de staging con metadata verificada.

## API nueva — OpenAPI `/api/v1`

- `POST /api/v1/dhole/quotes`: JWT M2M app-only, rol `dynamics.records.create` y grant de entidad lógica `quote`/operación `create` con columnas exactamente autorizadas. Necesita `confirmed:true`, `dholeRateId` GUID único y `Idempotency-Key: dhole-rate-{dholeRateId:N}`.
- `GET /api/v1/dhole/quotes/links/{dholeRateId}?connectionCode=staging`: JWT M2M, rol `dynamics.records.read`, grant `quote/read` sobre `quoteid`; devuelve GUID de oferta o estado de reconciliación.
- `GET /api/v1/records/quote/{guid}?connectionCode=staging&select=quoteid,quotenumber,name`: lectura general con proyección autorizada para recuperar número y otros campos, **solo cuando estén habilitados**.
- `GET /api/v1/write-operations/{operationId}?connectionCode=staging`: consulta solo del cliente que ejecutó la operación. Un resultado ambiguo se resuelve mediante Admin, nunca reintentando el POST.
- `POST /api/v1/templates/CreateQuote/execute`: motor genérico subyacente, no usar directamente para ofertas Dhole; no reserva el `DholeRateId`.

### Ejemplo de solicitud

```http
POST /api/v1/dhole/quotes HTTP/1.1
Authorization: Bearer <app-only-entra-jwt>
Content-Type: application/json
Idempotency-Key: dhole-rate-11111111111141118111111111111111
X-Correlation-ID: 33333333-3333-4333-8333-333333333333

{"connectionCode":"staging","dholeRateId":"11111111-1111-4111-8111-111111111111","confirmed":true,"inputs":{"name":"QUOTE TEST","customer_account_id":"22222222-2222-4222-8222-222222222222","currency_id":"44444444-4444-4444-8444-444444444444","pol":"Shanghai","pod":"Caldera","freight":1000}}
```

`201` primera creación confirmada, `200` vínculo previamente creado, `409` cuando PENDING/UNKNOWN/ambigüedad exige reconciliación. Una respuesta exitosa incluye `dholeRateId`, `dynamicsQuoteId`, `status`, `operationId`, `alreadyExisted` y `quoteNumber` (nulo hasta lectura autorizada). Nunca asumir que `502`, timeout o pérdida de respuesta permite reintentar con una nueva clave.

## Preparación sin secretos ni hardcodeo de Choice

1. Configure `AdminOidc` y `M2m:Authority/Audience` externos **independientes**. Cree una app Entra propia para DholeSystem/Pricing autorizada para obtener tokens client-credentials de esta API. No copie secretos de DynaConnect legacy.
2. En Admin, registre esa app (`POST /api/admin/v1/clients` con emisor verificado, `clientId` y `enabled:true`). Cree **solo** grants `quote/create` con los campos del template que sean escribibles y `quote/read` limitado a `quoteid`, `quotenumber` y datos necesarios. El tenant y OAuth client ID los asigna el administrador, nunca el paquete.
3. Descubra los metadatos reales de `quote`; habilite entidad, campos y relaciones (nombres de navegación `customerid_account` y `transactioncurrencyid` sujetos a descubrimiento). Compruebe que cada columna sea escribible, tipos y Choice numéricos según el entorno.
4. Importe el archivo `integration/phase10-createquote.template.json` por `POST /api/admin/v1/connections/{code}/templates/import`. Es **borrador inactivo**. Quite del mapping las columnas no disponibles, ajuste contratos tipados/Choice de acuerdo al snapshot, ejecute preview y active solo tras revisión explícita. El archivo deliberadamente no incluye `msdyn_invoicesetuptotals`: agréguelo solamente si la metadata confirma `CanCreate` y la política habilita escritura. No envíe nulidades opcionales.
5. Efectúe los casos negativos de aislamiento de conexiones, campos, permisos, moneda/cliente inexistentes, conflicto idempotente, pérdida de respuesta después del POST y reconciliación manual. Use registros ficticios de sandbox.
6. Configure el consumidor Dhole por HTTPS directo hacia el conector; secretos M2M desde secret manager, **nunca** desde repo, frontend, ni el gateway. El timeout y reintentos GET deben estar acotados; jamás repetir un POST de resultado incierto.
7. Flujo de aprobación: Pricing confirma la oferta formal y ejecuta la invocación; el CRM DholeSystem solo conserva el GUID recibido y consulta el estado. Desactive la ejecución automática hasta completar E2E en staging.

## Limitaciones y recuperación

El vínculo se reserva antes del POST mediante el índice único `(connection_id,dhole_rate_id)` de `dynamics.quote_links`, también compartido con la ruta histórica. Si se pierde la respuesta, `UNKNOWN` queda bloqueado para reenvíos. Las operaciones de escritura quedan en `dynamics.write_operations` con un ID para consultar y reconciliar. El vínculo configurable en `external_references` se escribe **después** de confirmar en PostgreSQL; si falla este paso, no se duplica la oferta: un operador restaura la referencia desde Admin verificando GUID real. El POST nuevo no ejecuta callbacks ni depende de Pricing para funcionar.

**Sin OAuth/metadata reales de staging no es válido afirmar que se crearon ofertas Dataverse ni que el E2E está aprobado.** El pipeline CI y las pruebas automáticas deben dar evidencia específica antes de declarar la fase cerrada.

### Reconciliación supervisada (solo SecurityAdmin)

Después de verificar **fuera del conector** en la organización real de Dataverse que la oferta existe y corresponde al rate, el administrador puede desbloquear el vínculo con:

```http
POST /api/admin/v1/connections/staging/dhole/quotes/11111111-1111-4111-8111-111111111111/reconcile
Content-Type: application/json
X-CSRF-TOKEN: <token-de-sesion-administrativa>

{"dynamicsQuoteId":"22222222-2222-4222-8222-222222222222","confirmedInDataverse":true,"evidence":"Dataverse quote manually verified in sandbox; approved by operator"}
```

Solo funciona desde `UNKNOWN`, nunca desde `PENDING`, y crea un evento de auditoría. Se rechazan IDs duplicados y el cambio de un vínculo ya confirmado. **No** asume que el GUID suministrado sea válido solo por formato: el operador tiene obligación de verificar que la oferta exista y sea la correcta. Esta acción no vuelve a enviar una escritura; el estado de la operación genérica se reconcilia por separado desde Admin.
