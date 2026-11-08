# Changelog

## [1.3.0] - 2026-11-08 (candidato F6.10, sin publicar)

- F6.10: conector Dynamics/Dataverse incorpora Outbox PostgreSQL propio y migración SQL `019_realtime_job_outbox.sql` versionada con checksum; crea invalidaciones PII-free al encolar/repetir trabajos autorizados en la misma transacción.
- Publicador opt-in a `dhole.dynamics.realtime.v1` usa `FOR UPDATE SKIP LOCKED` en Outbox para soportar varios procesos y reintentos por EventId; nunca comunica registros ni secretos Dataverse.
- El cliente solo puede reconciliar mediante la API del propietario y autorización Admin, manteniendo el aislamiento nativo del conector. Cuatro flags OFF por defecto, no modifica políticas de escritura Dataverse.
- SemVer componente 1.2.1 → 1.3.0, ecosistema candidato 1.13.0; sin despliegue ni merges.

## [1.2.1] - 2026-11-05

- Activa deploy automático de Dynamics por push a master/develop, sin aprobación manual de GitHub Environment.
- Mantiene preflights de PostgreSQL, host, flags de escritura, migraciones y healthchecks.
- Actualiza SemVer y metadatos de la versión global Dhole 1.3.1.

## [1.2.0] - 2026-11-05

- Fix high-severity vulnerabilities in Dynamics admin by upgrading vue-i18n and intlify dependencies to 11.4.16.

- Incorporates universal Dhole SemVer policy with GitHub Actions checks.
- Synchronizes service assembly metadata with the ecosystem version.
- Adds permanent Codex instructions and coordinated release requirements.
