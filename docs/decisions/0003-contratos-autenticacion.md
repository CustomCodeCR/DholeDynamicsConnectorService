# ADR 0003 — Separación de contratos e identidades

- **Estado:** Propuesto (Fase 00; pendiente revisión/aprobación)
- **Fecha:** 2026-10-17
- **Contexto:** `DholeDynamicsConnectorService` debe evolucionar desde prototipo Quote a plataforma Dataverse autónoma y gobernada.

## Decisión propuesta
Admin: /api/admin/v1 con OIDC/RBAC humano. Runtime: /api/v1 con M2M OAuth2 JWT y políticas por conexión/operación/campo; sin dependencia DholeAuth/DholeApiGateway. Contracts externos estables y versionados, OpenAPI y errores consistentes. Implementar F01/F03.

## Consecuencias y reversibilidad
Documentar pruebas de contrato/seguridad antes de implementar; toda excepción requiere un ADR nuevo, pruebas y aprobación. Este ADR **no** es código operativo ni permiso para desplegar.
