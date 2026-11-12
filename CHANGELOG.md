# Changelog

## [1.4.1] - 2026-11-12

- PATCH DH-20261112-DYNAMICS-QUO-613-619-118: reconoce una única línea de venta Dhole con cantidad 1 e importe igual a la revisión aceptada mediante lectura de Dataverse y RateId verificado (QUO-00000-000613), sin otra escritura ni pérdida del ledger.
- Rehabilita exclusivamente la recuperación con nueva generación de idempotencia cuando una operación de cabecera reservada/ambigua se ha conciliado MANUALMENTE como no aplicada por SecurityAdmin; jamás interpreta ausencia de recordId, timeout o HTTP 409 como prueba (QUO-00000-000619).
- Expone el ID de operación en el diagnóstico administrativo para poder auditar y conciliar la clave exacta. Mantiene guardas de línea enviada y estado 409, sin duplicados automáticos.
- Versión master 1.2.1 → 1.4.1 (backport focalizado); develop 1.4.0 → 1.4.1; ecosistema 1.43.6. CI/staging y comprobación real de Dataverse pendientes; no despliegue productivo autorizado.


## [1.2.1] - 2026-11-05

- Deploy automático Dynamics por push en develop y master sin requerir aprobación GitHub Environment.
- Conserva validaciones del host y protecciones PostgreSQL, CRUD y healthchecks.
- SemVer del componente y metadata Dhole 1.3.1.

## [1.2.0] - 2026-11-05

- Fix high-severity vulnerabilities in Dynamics admin by upgrading vue-i18n and intlify dependencies to 11.4.16.

- Incorporates universal Dhole SemVer policy with GitHub Actions checks.
- Synchronizes service assembly metadata with the ecosystem version.
- Adds permanent Codex instructions and coordinated release requirements.
