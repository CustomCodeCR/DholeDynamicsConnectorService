## [1.5.0] - 2026-11-14 (API Manager discovery)

- MINOR: inventario administrativo de rutas HTTP de Dynamics Connector, protegido por JWT y alcance integrations.thirdparty.manage, sin alterar OAuth ni operaciones Dataverse.
- Global Dhole 1.56.0.

# Changelog

## [1.4.1] - 2026-11-12

- PATCH DH-20261112-DYNAMICS-QUO-613-619-118: reconoce una única línea de venta Dhole con cantidad 1 e importe igual a la revisión aceptada mediante lectura de Dataverse y RateId verificado (QUO-00000-000613), sin otra escritura ni pérdida del ledger.
- Rehabilita exclusivamente la recuperación con nueva generación de idempotencia cuando una operación de cabecera reservada/ambigua se ha conciliado MANUALMENTE como no aplicada por SecurityAdmin; jamás interpreta ausencia de recordId, timeout o HTTP 409 como prueba (QUO-00000-000619).
- Expone el ID de operación en el diagnóstico administrativo para poder auditar y conciliar la clave exacta. Mantiene guardas de línea enviada y estado 409, sin duplicados automáticos.
- Versión 1.4.0 → 1.4.1; ecosistema 1.43.6. CI/staging y comprobación real de Dataverse pendientes; no despliegue productivo autorizado.

## [1.4.0] - 2026-11-09 (NotificationsHub universal realtime pilot; candidate)

- MINOR: retain domain Outbox/REST producer and scoped access under the existing NotificationsHub delivery model; CustomCodeFramework untouched.
- Ecosystem candidate 1.14.0 / 16 Dhole repositories, release `rt-20261107-01` in isolated branch `feature/rt-notificationshub-20261109-01`.
- Staging deployment, operational audit and final integration remain pending. Production flags OFF.


## [1.3.6] - 2026-11-08 (Dhole realtime coordinated hardening)

- PATCH 1.13.15 ecosystem release alignment with F2/F6 AOF read-only checks and F10 branch-preservation fix. Framework/connector candidate unchanged; realtime flags OFF and gates F6–F10 not formally approved.



## [1.3.5] - 2026-11-08 (F9 review candidate)

- PATCH SemVer global 1.13.14 para excepción F3 en revisión de F9. Mantiene gates formales, flags OFF y prohibición de despliegue hasta verificar staging y durabilidad.



## [1.3.4] - 2026-11-08 (candidato F3)

- Sincronización PATCH de la versión global 1.13.13 correspondiente al ensayo WSS/Traefik de DholeApiGateway; sin nuevas modificaciones funcionales ni despliegue.



## [1.3.3] - 2026-11-08 (F6/F9 staging-audit candidate)

- Sincroniza Dhole Realtime `rt-20261107-01` a versión global `1.13.12` tras incorporar auditoría real de persistencia en staging. PATCH 1.3.2 → 1.3.3; no modifica rutas, eventos, datos, contratos REST ni flags. No se fusiona hasta completar gates formales.



## [1.3.2] - 2026-11-08 (candidato F9, sin publicar)

- Sincroniza release global `rt-20261107-01` a `1.13.11` tras resolver los hallazgos de CI de Config, Reports y Gateway. Bump PATCH de DholeDynamicsConnectorService `1.3.1` a `1.3.2`, sin modificar REST, eventos, precios ni datos; flags realtime OFF y PR F9 aún DRAFT.



## [1.3.1] - 2026-11-08 (candidato F9, sin publicación)

- Sincroniza metadatos de versión global del batch Dhole Realtime `rt-20261107-01` a `1.13.10`; actualización PATCH independiente de DholeDynamicsConnectorService `1.3.0` → `1.3.1`. No cambia contratos REST, datos, lógica de negocio ni flags; requiere gates F0–F8 y CI de SHA final antes de merge.


- F7 C14 (candidato parcial): PostgreSQL real de CI prueba constraint de trabajos Dynamics repetidos y un único Outbox usando esquema real sql/010 + sql/019, Actions #42507593928 PASS. Falta probar correo, Auth y entorno staging.

- F6.10 (candidato): GET privilegiado `/api/admin/v1/operations/realtime/jobs/{id}` devuelve estado mínimo del job para recuperación REST tras SignalR, sin claves/ID de conexión, campos de Dataverse, dedupe o payload; Admin + SecurityAdmin intactos.


## [1.3.0] - 2026-11-08 (candidato F6.10, sin publicar)

- F6.10: conector Dynamics/Dataverse incorpora Outbox PostgreSQL propio y migración SQL `019_realtime_job_outbox.sql` versionada con checksum; crea invalidaciones PII-free al encolar/repetir trabajos autorizados en la misma transacción.
- Publicador opt-in a `dhole.dynamics.realtime.v1` usa `FOR UPDATE SKIP LOCKED` en Outbox para soportar varios procesos y reintentos por EventId; nunca comunica registros ni secretos Dataverse.
- El cliente solo puede reconciliar mediante la API del propietario y autorización Admin, manteniendo el aislamiento nativo del conector. Cuatro flags OFF por defecto, no modifica políticas de escritura Dataverse.
- SemVer componente 1.2.1 → 1.3.0, ecosistema candidato 1.13.0; sin despliegue ni merges.


## [1.2.1] - 2026-11-05

- Activa deploy automático de Dynamics por push a master/develop, sin aprobación manual de GitHub Environment.
- Mantiene preflights de PostgreSQL, host, flags de escritura, migraciones y healthchecks.
- Actualiza SemVer y metadatos de la versión global Dhole 1.3.1.


### Notas conservadas de master


- Deploy automático Dynamics por push en develop y master sin requerir aprobación GitHub Environment.
- Conserva validaciones del host y protecciones PostgreSQL, CRUD y healthchecks.
- SemVer del componente y metadata Dhole 1.3.1.


## [1.2.0] - 2026-11-05

- Fix high-severity vulnerabilities in Dynamics admin by upgrading vue-i18n and intlify dependencies to 11.4.16.

- Incorporates universal Dhole SemVer policy with GitHub Actions checks.
- Synchronizes service assembly metadata with the ecosystem version.
- Adds permanent Codex instructions and coordinated release requirements.
