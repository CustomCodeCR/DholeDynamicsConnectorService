# ADR 0004 — Errores estables e idempotencia

- **Estado:** Propuesto (Fase 00; pendiente revisión/aprobación)
- **Fecha:** 2026-10-17
- **Contexto:** `DholeDynamicsConnectorService` debe evolucionar desde prototipo Quote a plataforma Dataverse autónoma y gobernada.

## Decisión propuesta
ProblemDetails con código estable, correlationId y sanitización; 401/403/404/409/412/429/5xx correctos. Idempotency-Key acotada por cliente/conexión/acción+hash; si el POST es ambiguo, marcar UNKNOWN, bloquear reenvío y conciliar antes de terminar. Implementar F01/F06/F11.

## Consecuencias y reversibilidad
Documentar pruebas de contrato/seguridad antes de implementar; toda excepción requiere un ADR nuevo, pruebas y aprobación. Este ADR **no** es código operativo ni permiso para desplegar.
