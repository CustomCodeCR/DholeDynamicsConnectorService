# Puntos de integración previstos — Fase 00

**No se modificaron otros repositorios.** El diseño define contratos, no conexiones en ejecución.

| Sistema | Evidencia inspeccionada | Límite propuesto |
|---|---|---|
| `CustomCodeCR/DholeSystem` | `README.md` indica repositorio de documentación, no servicio ejecutable; `docs/requirements/02-pricing-department.md` menciona flujo actual dependiente de Dynamics | Cliente externo HTTP; token M2M de DynaConnect y permisos específicos |
| `CustomCodeCR/DholePricingService` | `src/Dhole.Pricing.Api/Program.cs` no registra Dataverse explícitamente; no se inspeccionó exhaustivamente todo el código de este repositorio | Recibe/produce RateId y cotización, pero DynaConnect no lo importa ni arranca con él |
| `CustomCodeCR/DholeDynamicsConnectorService` | `QuoteCreateRequest` incluye `DholeRateId` y `QuoteRepository` almacena vínculo a GUID | Generalizar `external_references` a cualquier `sourceSystem/sourceType/sourceId` |

## Contrato futuro
- Base URL propia con TLS, versiones `/api/v1` y `/api/admin/v1` separadas.
- OAuth2 M2M propio con `client_id`, scopes, conexiones/entidades autorizadas y límite de cuotas.
- `POST .../templates/CreateQuote/execute`: input tipado, cliente y moneda mapeados, `Idempotency-Key`, `operationId` y estado.
- `GET /api/v1/operations/{operationId}` y referencias: reconciliación y lectura posterior a timeout.
- Configuración Dataverse se mantiene dentro del conector; Dhole nunca recibe client secret de Microsoft.
- No comunicación obligatoria por `DholeApiGateway`, ni consumidor por la base de datos.

**Pendientes deliberados:** localizar consumidores ejecutables, confirmar callbacks o polling, contratos de Quote y scopes en Fase 10; no inferir despliegues a partir de documentos.
