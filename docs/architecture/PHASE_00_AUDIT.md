# Auditoría de línea base — Fase 00

Fecha: 2026-10-17. Solo inspección remota de repositorios; no hubo despliegue, DB real ni credenciales utilizadas.

## Repositorio principal
- Repositorio: `CustomCodeCR/DholeDynamicsConnectorService`, público al inspeccionarlo, `main` inicial en `83da32be5f54e7bce07bf523ad0a291848d1f62c`. Antes de este trabajo no existían `master` ni `develop`; se derivaron sin alterar `main`.
- Árbol inspeccionado: 13 archivos rastreados, principalmente API Minimal .NET 10, SQL DDL y script de provisión. Sin frontend propio, suites de pruebas, Dockerfile ni CI en ese árbol.
- **Prototipo aprovechable:** `Encryption.cs` (AES-256-GCM con AAD por conexión, clave externa obligatoria), `QuoteRepository.cs` (reserva de IDs y estado desconocido), `Security.cs` (restricción inicial URL Dynamics y scopes), `sql/001_init.sql` (conexiones, quote links, referencias, auditoría).
- **No listo para producto autónomo:** `Program.cs` presenta rutas `/api/dynamics/*` para Quote y conexiones, sin contratos independientes `/api/v1` y `/api/admin/v1`; las dependencias y las reglas de negocio se mantienen dentro de una sola API.
- `DataverseClient.cs` fija ruta `quotes`, campos y códigos de Choice para CreateQuote. No hay descubrimiento de metadatos, autorización por columna/entidad/conexión, saved queries, paginación ni motor CRUD genérico.
- `Program.cs` valida scopes genéricos pero no vincula identidad cliente a conexión autorizada. La clave simétrica JWT en configuración no constituye por sí sola la autenticación humana OIDC y M2M desacopladas requeridas.
- `sql/001_init.sql` usa esquema `dynamics` y nombre de DB propuesto por scripts `dhole_dynamics`; el plan apunta a DB dedicada `dhole_dynamics_connector` y esquema configurable como `dynaconnect`. **Decidir migración compatible, nunca renombrar/destruir datos a ciegas.**
- `scripts/provision-database.sh` propone mismo clúster de PostgreSQL que Dhole. Una **base lógica dedicada**, cuenta/credenciales y backups autónomos pueden coexistir en host compartido, pero la implementación definitiva debe comprobar aislamiento y evitar dependencias operacionales inaceptables.

## Legado `MauLang18/DynaConnect`
- `DynamicsCF/DynamicsCF/Controllers/ValuesController.cs`: **88 endpoints: 87 GET + 1 PATCH**; matriz individual `docs/migration/LEGACY_COVERAGE.csv` y resumen `LEGACY_INVENTORY.md`.
- Proyecto legado ASP.NET Core 6, controlador monolítico, consultas OData construidas como strings con interpolación de parámetros, configuración de autenticación en cada método y credenciales incrustadas en código. **No copiar ni imprimir los valores.**
- El árbol incluye archivos `.vs`, `bin`, `obj`, perfiles de publicación y `appsettings`; tratar configuración histórica y artefactos como superficie potencial de fuga. Ver procedimiento `docs/security/LEGACY_SECRET_ROTATION.md`.
- Tablas de las rutas por código: `incidents`, `accounts`, `quotes`, `competitors`, `salesliteratures`. Las dos últimas están asociadas a aduanas/exoneraciones en el legado, por lo que requieren especial verificación con metadatos.
- Ningún endpoint antiguo queda sin categoría de destino; **ninguno** se declara migrado.

## Consumidores (solo puntos de integración; repositorios no editados)
- `CustomCodeCR/DholeSystem`: repositorio de documentación de microservicios, no ejecutable. La especificación de Pricing menciona dependencia operativa de Dynamics, pero no hay cliente del conector demostrado en este repositorio.
- `CustomCodeCR/DholePricingService`: API de Pricing con arquitectura propia; la inspección de `src/Dhole.Pricing.Api/Program.cs` no mostró referencias explícitas a Dynamics/Dataverse. Esto **no prueba** la ausencia de referencias en otros archivos.
- Contrato futuro, no implementado: `DholeSystem/Pricing -> HTTPS -> OAuth M2M -> /api/v1 -> políticas del conector -> Dataverse`; envío Quote con vínculo RateId ↔ DynamicsQuoteId e idempotencia. Sin usar `DholeApiGateway`, `DholeAuth` ni base de Dhole como dependencia del conector.

## Brechas y prioridades
| Riesgo | Severidad | Acción planificada |
|---|---|---|
| Credenciales históricas en controlador público | Crítica | Inventariar aplicaciones, revocar y rotar secretos, auditar uso, escaneo historia |
| Rutas sin autorización por conexión/entidad/campos | Alta | ADR políticas + enforcement F03 |
| OData interpolado y códigos Choice fijos | Alta | DSL parametrizado y metadatos F04–F07 |
| Dependencia conceptual de Dhole en nombres JWT/DB | Alta | Contratos y credenciales independientes F01–F03 |
| Escrituras ambiguas tras caída | Alta | Reconciliación duradera F06/F11 |
| Ausencia de CI y pruebas | Alta | Solución y suites F01 |
| DB y despliegue sin validación de independencia | Media/Alta | F02/F12 |

**Resultado Fase 00:** propuesta documental lista para revisión; **no aprobar todavía** hasta revisión de arquitectura, seguridad y mitigación del incidente potencial de credenciales. No iniciar F01 por inercia.
