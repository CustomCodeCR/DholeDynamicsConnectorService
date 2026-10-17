# ADR 0007 — Jobs y resiliencia

- **Estado:** Propuesto (Fase 00; pendiente revisión/aprobación)
- **Fecha:** 2026-10-17
- **Contexto:** `DholeDynamicsConnectorService` debe evolucionar desde prototipo Quote a plataforma Dataverse autónoma y gobernada.

## Decisión propuesta
Cola persistente con checkpoint, backoff/jitter, Retry-After, dead-letter, DLQ revisable y observabilidad. Nunca repetir una escritura remota de resultado desconocido de manera automática. Redis opcional propio, no compartir cola Dhole. Implementar F11.

## Consecuencias y reversibilidad
Documentar pruebas de contrato/seguridad antes de implementar; toda excepción requiere un ADR nuevo, pruebas y aprobación. Este ADR **no** es código operativo ni permiso para desplegar.
