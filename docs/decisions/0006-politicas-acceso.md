# ADR 0006 — Gobernanza y políticas

- **Estado:** Propuesto (Fase 00; pendiente revisión/aprobación)
- **Fecha:** 2026-10-17
- **Contexto:** `DholeDynamicsConnectorService` debe evolucionar desde prototipo Quote a plataforma Dataverse autónoma y gobernada.

## Decisión propuesta
Deny by default por cliente/usuario, conexión, entity logical name, operación, columna, navegación y saved query. Los cambios son versionados, revisables y reversibles. No permitir elusión a través de metadata o URLs libres. Implementar F03–F07.

## Consecuencias y reversibilidad
Documentar pruebas de contrato/seguridad antes de implementar; toda excepción requiere un ADR nuevo, pruebas y aprobación. Este ADR **no** es código operativo ni permiso para desplegar.
