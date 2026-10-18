# Fase 12 — Endurecimiento, despliegue independiente y cierre

## Entrega técnica

- **Entorno independiente**: `compose.release.yaml` define PostgreSQL dedicado (`dhole_dynamics_connector`), migrador, API, worker separado, Vue/Nginx sin root y Caddy HTTPS. Solo Caddy publica 80/443; Postgres no expone puertos y los otros servicios quedan en redes internas. La API y worker tienen una salida de red dedicada para Entra/Dataverse, sin compartir la infraestructura de DholeSystem/Pricing.
- **Sin credenciales en Git**: cinco secretos de API y certificados para DataProtection se leen de ficheros Docker Secret en `/run/secrets`; además Postgres obtiene su contraseña por un secret independiente. Los volúmenes `connector_data`, `data_protection_keys`, `caddy_data` son persistentes. Cookies OIDC tienen llaves DataProtection en volumen cifradas con certificado PFX externo.
- **Aislamiento staging/producción**: cada instalación usa host, DNS, stack Compose, volúmenes, credenciales, usuario Entra, client ID, llaves de cifrado, receptor age y certificados independientes. Nunca reutilice llaves/BD entre entornos. `DYNAMICS_ENVIRONMENT=Staging|Production`; los ejemplos no contienen contraseñas.
- **Release controlado**: pipelines `connector-ci`, `connector-release-security` (CodeQL C#/JS, secretos y preflight), `connector-release-images` (solo disparo manual desde master, verifica SHA y security gate, publica tres imágenes GHCR con etiqueta de 40 caracteres SHA, SBOM y provenance) y `connector-deploy-reviewed` (solo despacho manual con aprobación de GitHub Environment y ticket coincidente en host). No hay despliegue por push ni auto producción. Los scripts de despliegue/rollback nunca invocan `docker compose down -v`.
- **Protección de producción**: las operaciones de escritura fallan cerradas hasta tener `ProductionWrites:Approved=YES` + un ticket válido, configurados **fuera de Git**. Las eliminaciones, así como acciones Dataverse potencialmente destructivas, exigen adicionalmente `ProductionWrites:DeletesApproved=YES` y un segundo ticket. El transporte Dataverse y el middleware HTTP aplican ambos controles; batch aplica política por comando. Las lecturas, administración de accesos y operaciones de reconciliación segura continúan disponibles.
- **Rollback**: script `scripts/release-rollback.sh` restaura una imagen anterior SHA aprobada **sin** revertir migraciones ni purgar el volumen. Requiere respaldo previo y comprobar compatibilidad del esquema. Recovery desde dump cifrado se verifica en CI sobre sandbox de BD vacía, no en producción.
- **Observabilidad**: `/health/ready`, `/health/jobs`, auditoría saneada, estadísticos y dead letters de Fases 08/11; CSP, anti-clickjacking, no-sniff y HSTS en borde TLS.

## Acceptance gates y evidencia

| Bloqueador | Método | Estado de partida |
|---|---|---|
| Build, .NET tests, frontend, análisis secretos | GitHub Actions `connector-ci` | CI se ejecuta en master/develop; consulte run SHA exacto |
| SAST C#/JavaScript | CodeQL `connector-release-security` | Se ejecuta en master/develop; aprobar antes de crear imágenes |
| Topología Compose de release | `scripts/release-preflight.sh` | Validación estática en CI; no despliega |
| Migraciones y rollback de esquema | Migraciones 001–010 tamper-evident, backup y sandbox restore | Prueba automatizada en CI; downgrade SQL prohibido |
| TLS, DNS, firewall y permisos mínimos de host | Revisión de staging y `release-smoke.sh` | **PENDIENTE**: datos reales de infraestructura |
| Entra OIDC + M2M, Dataverse real, CRUD/Quote/sync | Matriz E2E en [Staging E2E](runbooks/PHASE_12_STAGING_E2E.md) | **PENDIENTE**: credenciales y autorización del tenant |
| Restore con llave real e incidente simulado | Runbook y acta de recovery | **PENDIENTE**: ejecutar en entorno staging |
| Habilitación escritura/eliminación en prod | Doble aprobación explícita externa + ticket | **NO AUTORIZADO**: bloqueado por defecto |
| Go-live / rollout gradual | Revisión formal de checklist, primero staging | **PENDIENTE** |

Las pruebas de CI, por sí solas, no prueban éxito E2E en Microsoft Dataverse. No informar `go-live aprobado` ni desplegar producción hasta completar cada gate operativo. **No se realiza despliegue externo desde esta entrega**.

Documentación complementaria:
- [Manual de despliegue y operación](runbooks/PHASE_12_DEPLOYMENT.md)
- [Plan E2E y checklist de go-live](runbooks/PHASE_12_STAGING_E2E.md)
- [Manual Admin](manuals/PHASE_12_ADMIN_MANUAL.md)
- [Manual M2M consumidor](manuals/PHASE_12_CONSUMER_MANUAL.md)
- [Incidentes, SLO, recuperación y rotación](runbooks/PHASE_12_OPERATIONS.md)
- [Recuperación de sincronización fase 11](runbooks/PHASE_11_RECOVERY.md)

## Rollback y riesgos abiertos

1. Desactivar `DYNAMICS_JOBS_ENABLED` ante un job erróneo; conservar checkpoints y cola.
2. Revertir **imagen** a SHA anterior mediante script y ticket de operación, nunca revertir SQL manualmente.
3. Si una migración no es compatible con imagen previa, mantener servicio en modo read-only y aplicar parche forward. No hay downgrade automático.
4. Las claves históricas expuestas en el repositorio legacy deben revocarse y documentarse externamente: solo un operador Entra puede acreditar la revocación.
5. El control de red de egress a dominios permitidos debe aplicarse en firewall del host/cloud; Compose no constituye una allowlist IP de Microsoft. ACL Admin adicional en firewall / VPN / identity-aware gateway recomendado.
6. No se intentó login real, creación ni borrado real de Dynamics sin credenciales y autorización de staging; las pruebas sintéticas no sustituyen esos requisitos.
