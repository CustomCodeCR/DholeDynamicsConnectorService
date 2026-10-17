# ADR 0005 — Secretos y rotación

- **Estado:** Propuesto (Fase 00; pendiente revisión/aprobación)
- **Fecha:** 2026-10-17
- **Contexto:** `DholeDynamicsConnectorService` debe evolucionar desde prototipo Quote a plataforma Dataverse autónoma y gobernada.

## Decisión propuesta
AES-256-GCM con nonce único y AAD por conexión, clave maestra externa o vault; versionado de claves con migración segura, jamás publicar tokens, secretos, cipher text descifrado en UI. Plan de incidente en LEGACY_SECRET_ROTATION.md. Implementar controles F02/F03.

## Consecuencias y reversibilidad
Documentar pruebas de contrato/seguridad antes de implementar; toda excepción requiere un ADR nuevo, pruebas y aprobación. Este ADR **no** es código operativo ni permiso para desplegar.
