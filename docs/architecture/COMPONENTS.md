# Arquitectura objetivo — Fase 00

La solución es independiente y configurable. Los diagramas son **diseño acordable, no implementación existente**.

```mermaid
flowchart LR
 A[Operador autorizado] -->|OIDC| UI[Admin Vue 3]
 UI -->|JWT /api/admin/v1| API[API independiente .NET 10]
 C[DholeSystem / Pricing y clientes externos] -->|HTTPS OAuth2 M2M| R[/api/v1]
 R --> API
 API --> AUTH[Autorización conexión / entidad / operación / columnas]
 AUTH --> ENGINE[Motor genérico + templates + queries]
 ENGINE --> PG[(PostgreSQL dedicado)]
 ENGINE --> KV[Clave externa / Vault]
 ENGINE --> DV[Dataverse Web API v9.2]
 ENGINE --> Q[Workers + cola durable]
 Q --> PG
```

## Límites por componente
- **Api:** endpoints versionados, autenticación independiente Admin/M2M, validación, correlation ID y ProblemDetails; sin resolver entity-set desde el cliente.
- **Contracts:** DTO y filtros tipados públicos; no exponer token, URL de Dataverse, ni secretos.
- **Application:** políticas, commands, queries, plantillas, idempotencia, reintentos seguros y jobs.
- **Domain:** configuración y versiones, referencias externas, estados de operación e invariantes.
- **Infrastructure:** HTTP Dataverse + MSAL, metadata provider, sanitización, OpenTelemetry, secretos/vault.
- **Persistence:** configuración, políticas, snapshot metadata, auditoría, jobs y vínculos de sistemas externos; no clonar datos enteros de Dataverse.
- **Workers:** outbox/journal y reconciliación sin reintentar operaciones ambiguas.
- **UI:** solo consola administrativa; formularios definidos por metadatos y RBAC.

## Invariantes
1. Prohibido proxy OData libre; tablas, entity sets y relaciones se descubren y habilitan.
2. Per-request authorization: sujeto + cliente + conexión + tabla + operación + atributos, deny by default.
3. OAuth M2M de consumidores y OIDC Admin con autoridades, audiencias y permisos diferenciados.
4. Separación staging/producción por tenants, organizaciones, claves, cuentas y DB.
5. Auditoría sin payloads sensibles; cambios de política y plantillas versionados y reversibles.
6. `DholeApiGateway`, `DholeAuth`, `DholePricingService` y PostgreSQL de Dhole no son dependencias de arranque/runtime del conector.
