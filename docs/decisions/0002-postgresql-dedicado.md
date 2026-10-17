# ADR 0002 — PostgreSQL dedicado

- **Estado:** Propuesto (Fase 00; pendiente revisión/aprobación)
- **Fecha:** 2026-10-17
- **Contexto:** `DholeDynamicsConnectorService` debe evolucionar desde prototipo Quote a plataforma Dataverse autónoma y gobernada.

## Decisión propuesta
Base de datos y credenciales propias, almacenamiento solo de configuración, metadatos, referencias, auditoría y trabajos; no espejo completo de Dataverse. Decisión pendiente: esquema/nombre destino y convivencia segura con clúster físico existente. Migrar el prototipo sin DROP ni pérdidas; cifrado y backups probados en F02.

## Consecuencias y reversibilidad
Documentar pruebas de contrato/seguridad antes de implementar; toda excepción requiere un ADR nuevo, pruebas y aprobación. Este ADR **no** es código operativo ni permiso para desplegar.
