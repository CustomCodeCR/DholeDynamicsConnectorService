# Manual de despliegue independiente — staging y producción

## Preparación de host y DNS

1. Provisione **dos hosts o grupos de VM separados** (staging y producción), Linux actualizado y Docker Compose v2. No instale Dhole Redis, ni conecte bases DholeAuth/DholeSystem/Pricing.
2. Cree registro DNS A/AAAA para cada FQDN público distinto. Caddy valida ACME por 80/443, renueva el certificado y guarda material en `caddy_data`. Use un correo operativo válido en `DYNAMICS_ACME_EMAIL`.
3. Firewall de entrada: publicar **únicamente 80/TCP, 443/TCP y 443/UDP** desde Internet; restringir 22/TCP a IPs del operador/runner. No exponer 5432, 8080, 8081 ni la consola Docker. Si el Admin es solo interno, exija VPN o pasarela adicional en el borde.
4. Egress: permitir DNS, autoridad Microsoft Entra, host Dataverse aprobado, ACME, registry GHCR, actualizaciones y destino de backups. Restringir por firewall de host/cloud y monitorizar DNS; las políticas internas de la aplicación también fijan el dominio CRM.
5. Use un operador Linux sin sudo innecesario, con acceso al Docker daemon limitado a operaciones autorizadas. Docker es privilegio root equivalente. Habilite auditoría de login y host; la cuenta de servicios de Dataverse solo tiene roles mínimos para las tablas habilitadas.

## Ficheros secretos (no en Git)

Cree en cada host `/etc/dhole-dynamics/{staging|production}/release.env` a partir de `deploy/release.env.example`. Propietario operador, `chmod 600`, ruta absoluta. Cada secreto está en fichero privado, identificado en esa configuración:

- `postgres_password` y `connector_connection` apuntan a la misma **contraseña generada** de una base dedicada; cadena de conexión: `Host=postgres;Port=5432;Database=dhole_dynamics_connector;Username=connector_app;Password=...`. Nunca publique la cadena en logs.
- `dynamics_encryption_key` (32 bytes criptográficamente aleatorios en Base64), `read_cursor_key` (otra clave independiente de 32 bytes Base64).
- `admin_oidc_client_secret`: secreto OIDC admin **staging distinto de producción**.
- `data_protection_cert.pfx` y `data_protection_password`: certificado con clave privada para cifrar el anillo persistente de cookies; renovar antes del vencimiento conservando acceso a claves anteriores.
- `DYNAMICS_ADMIN_OIDC_AUTHORITY`, `DYNAMICS_M2M_AUTHORITY`: HTTPS del tenant correcto (no token ni secreto). `DYNAMICS_*_CLIENT_ID` y `AUDIENCE` diferentes entre entornos.
- `DYNAMICS_STAGING_APPROVAL_TICKET` o `DYNAMICS_GO_LIVE_TICKET`: ticket de cambio firmado fuera de la plataforma, debe coincidir con el lanzamiento del workflow.
- Respaldo `age`: receptor/llave de descifrado independientes y fuera del host DB, backup programado vía `scripts/backup-encrypted.sh` (el Compose no sustituye el cron externo para snapshots).

Comprobar que **los archivos montados** en `/run/secrets` son legibles por la identidad del servicio; no usar claves nulas ni defaults de desarrollo. Docker Compose en modo standalone presenta los secretos como ficheros; restringir estrictamente los permisos del directorio host. El volumen de llaves de sesión se prepara por `prepare-keys` con UID de la imagen .NET.

## Secuencia exacta

1. Ejecute `connector-ci` y `connector-release-security` sobre el mismo SHA revisado.
2. Dispare manualmente `connector-release-images` en `master` con ese SHA: publica `sha-<40hex>` para API, UI y Tools en GHCR. Verifique acceso de lectura del host al registry con credencial **read:packages**.
3. Configurar en GitHub dos Environments distintos (`staging`, `production`) con **required reviewers** y restricciones de branch. Definir Secrets de environment `DYNAMICS_RELEASE_SSH_KEY`, `DYNAMICS_RELEASE_SSH_KNOWN_HOSTS`, `DYNAMICS_RELEASE_HOST`, `DYNAMICS_RELEASE_USER` y variable `DYNAMICS_RELEASE_ENV_FILE`. Utilizar host key fijada de forma independiente, no `ssh-keyscan` dinámico.
4. En staging, aprobar el workflow `connector-deploy-reviewed`, con `target=staging`, SHA exacto y ticket coincidente con host. El workflow copia únicamente YAML, Caddyfile y scripts revisados por SSH de forma autenticada al host; el script ejecuta preflight, pull de imágenes, migraciones, arranque y smoke HTTPS.
5. Verificar login OIDC real, grants M2M, metadata, query, CRUD controlado, template Quote y delta según [matriz E2E](PHASE_12_STAGING_E2E.md). Asegure backup age previo y prueba de restauración con la **misma clave externa** en sandbox separado.
6. Para producción primero realizar snapshot y aprobación del comité, SAST sin hallazgos críticos, smoke de staging y acta E2E. Workflow requiere `production_confirmation=DEPLOY_PRODUCTION`, URL de evidencia staging, ticket y aprobación del Environment. El host además exige `DYNAMICS_GO_LIVE_APPROVED=YES`.
7. **No habilitar escrituras de producción durante el go-live técnico**. El sistema requiere otra decisión separada: `DYNAMICS_PRODUCTION_WRITES_APPROVED=YES` y ticket `DYNAMICS_PRODUCTION_WRITES_TICKET`; DELETE/acciones necesitan otro ticket y flag. Mantener ambos en NO durante aceptación.

## Fallo de despliegue, rollback y validaciones

`bash scripts/release-smoke.sh https://fqdn` valida certificado TLS, `/health/live`, `/health/ready`, 401 Admin anónimo y 401 M2M anónimo, pero **no** acceso real al tenant.

Una migración que falle **no debe forzar** el nuevo API. Se aborta la actualización y se revisa el registro del migrador sin volcar secretos. Si la nueva imagen falla después, ejecutar `DYNAMICS_ROLLBACK_APPROVED=YES bash scripts/release-rollback.sh /etc/dhole-dynamics/staging/release.env sha-<known-good>`, previa compatibilidad de esquema y autorización; no revertir automáticamente SQL.

Todos los comandos `docker compose -f compose.release.yaml` deben incluir la combinación de `--env-file` y `-p` del stack elegido. `docker compose down -v` está **prohibido** en releases. Backup y restore están en scripts Fase 02; restaurar **únicamente** en una BD `_restore_<suffix>` vacía, nunca sobre producción. Verificar conteo de migraciones, descifrado de secretos con la clave externa original, RPO y RTO. Registrar evidencia, no registrar la clave en ticket.

## Configuración de redes y permisos

`postgres` únicamente en `connector_internal` sin published port. `api` y `worker` comparten red interna para DB y `connector_egress` para Entra/Dataverse. `https` comparte red interna para entrar a Nginx Admin y una red de borde para 80/443. Los procesos .NET son no-root y los secretos jamás se incluyen en las imágenes o labels. CSP, HSTS y cookies seguras complementan RBAC/ABAC; no sustituyen firewall y permisos Entra.
