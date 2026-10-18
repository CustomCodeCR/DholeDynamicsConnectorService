# Operación y respuesta a incidentes — Fase 12

## SLOs propuestos (no resultados medidos)

- Disponibilidad objetivo API + Admin: **99.9% mensual**, excluyendo ventanas de mantenimiento preaprobadas. No atribuir caídas de Microsoft Dataverse a la disponibilidad de la base propia sin segmentar causa.
- Latencia local de rutas GET y health: p95 objetivo menor de **1 s** sin incluir tiempo externo de Dataverse; para llamadas Dataverse medir p95 y 429 por conexión por separado.
- Cola: alerta si más de 15 minutos sin progreso en una tarea habilitada o cualquier dead-letter no reconocida.
- Recovery objectives a confirmar por propietarios: RPO **24 h** con respaldo diario verificado; RTO **4 h** con imagen anterior, PKI y restore seguro. Estos valores son **objetivos**, no garantías verificadas.
- Retención de jobs completados/observaciones/manifiestos: por defecto 30 días; no borrar ledger idempotente ni dead-letter abierto. La retención de backups se define por cumplimiento/operaciones y no por la API.

## Chequeo diario (operaciones)

Revisar TLS Caddy con vencimiento ACME, `/health/ready`, `/health/jobs`, errores 5xx, p95, 429, alerts y dead-letter; comprobar backups cifrados generados y su edad, espacio libre en volúmenes y desvío del reloj. Verificar que no haya secretos, nombres de clientes, payloads o Authorization en logs/CSV. Evaluar grants y client apps expiran/revocadas semanalmente. Revisar Dependabot, NuGet/pnpm auditoría y CodeQL en cada release.

## Incidente y escalamiento

1. Abrir ticket y clasificar impacto (S1: datos/secretos/duplicación/escrituras no aprobadas; S2: degradación o caída de procesos; S3: fallos de ejecución no críticos).
2. S1: apagar Jobs en entorno, deshabilitar temporalmente permisos de escritura en configuración externa y en Entra, conservar DB/ledger/backups. Revocar identidad comprometida en Entra y rotar certificados/llaves con custodios designados.
3. S2: aislar causa (DNS/TLS, DB, migración, cuota Dataverse, OIDC), recopilar logs saneados/correlation ID; nunca repetir post ambiguo.
4. Si rollout falla, activar `scripts/release-rollback.sh` con `DYNAMICS_ROLLBACK_APPROVED=YES` y SHA previo compatible. Downgrade de BD NO soportado; parche forward o fail-safe read-only.
5. Restauración: generar backup `pg_dump | age` con llave externa, levantar DB `dhole_dynamics_connector_restore_<name>` vacía, autorizar `ALLOW_SANDBOX_RESTORE=YES` y ejecutar `scripts/restore-sandbox.sh`. Verificar integridad/migraciones y acceso a secrets con el anillo de claves correcto. Prohibido restaurar sobre producción.
6. Reportar RCA sin secretos, acciones, aprobadores, resultado y métricas de duración. Confirmar SLOs e impacto comercial en acta.

## Rotación de claves

Entra: rotar client secret y revocar el anterior tras confirmar conectividad. Llaves AES-GCM: proporcionar keyring externo versionado, `rotate-keys --dry-run` en BD de staging, validar backup, `--apply` solo con `DYNAMICS_ROTATION_APPROVED=YES`. Mantener versiones viejas hasta verificar snapshots/backups. Certificado DataProtection PFX: renovar conservando acceso a certificados históricos que protegen llaves ya persistidas; cookies existentes pueden invalidarse si se revocan sin plan. Separar plenamente staging y producción.

## Pruebas de desastre

Trimestral: restauración cifrada en sandbox y reconexión con credenciales de staging, caída de worker/reanudación, simular HTTP 429 y 5xx, actualización rota y rollback de imagen, verificación de política de write/delete en Production. Registrar resultados PASS/FAIL/NOT_RUN, tiempos RPO/RTO y ajustes de controles. Nunca generar borrados reales en organización de producción para probar recuperación.
