# Runbook — Recuperación de jobs y sincronización (Fase 11)

## Activar de forma controlada

1. En staging, comprobar `/health/ready` y que migración 010 esté aplicada.
2. Configurar organización Dataverse con snapshot reciente y permisos mínimos de solo lectura (`PrimaryId`; también `modifiedon` para watermark).
3. Usar OIDC SecurityAdmin para crear `sync_config`, iniciando deshabilitada. No admitir URLs externas ni parámetros OData arbitrarios.
4. Establecer `Jobs__Enabled=true` en configuración segura del proceso; revisar `/health/jobs` y métricas. Esto **no** despliega ni enciende jobs en producción.
5. Habilitar una entidad y observar una ejecución manual antes de programar intervalos.

## En caso de 429/5xx o Dataverse caído

- Revisar `/api/admin/v1/operations/stats`, `/alerts` y jobs filtrados por conexión. Registrar correlation ID, tipo de falla y marca temporal, nunca token ni URL delta completa.
- El job pasa a `pending` con backoff acotado y respeta Retry-After. Después de tres fallos por conexión el circuito pausa nuevas llamadas delta/metadata cinco minutos.
- Si agota intentos, queda `dead` y crea alerta. Verificar primero conectividad y permisos reales; luego `POST /jobs/{code}/{id}/replay` como SecurityAdmin con confirmación y evidencia. El replay del job no envía escrituras a Dataverse.

## Caída de API o restart durante sync

- Volver a arrancar con la **misma** BD dedicada. El lease de un proceso anterior expira (máximo 120 s sin heartbeat) y otro worker lo reclama si quedan intentos.
- `sync_checkpoints` y `sync_changes` se guardan juntos, sin cambios parcialmente visibles. Inspeccionar versión/páginas/lastSuccessAt desde `/sync/{code}/checkpoints`, nunca solicitar enlaces opacos desde la UI.
- Para saltar de delta a watermark, documentar la pérdida de tombstones; efectuar el cambio por SecurityAdmin. Cambiar modo **reinicia** el cursor. No alterar checkpoints por SQL manual.
- Si dos instancias compiten, el `lease_version` impide que la anterior confirme cambios una vez perdido el lease.

## Escritura ambigua o reconcilación

- El job `reconcile_scan` solo genera una alerta. Ni el worker ni el administrador deben asumir que un timeout significa operación no aplicada.
- Usar Fase 06 `/write-operations/{id}` y, para oferta, Fase 10 `/dhole/quotes/{rateId}/reconcile` después de verificar en Dataverse. Nunca crear un nuevo POST ni otra clave idempotente como workaround.

## Retención y auditoría

- La política de 7–365 días borra solamente jobs completados, observaciones HTTP, alertas ya reconocidas y hashes de cambios. Las filas dead-letter y el ledger idempotente permanecen.
- Exportar acciones saneadas desde `/audit/export` solo con rol SecurityAdmin. Conservar respaldos cifrados/rotación de claves según Fase 02.
- Alertas `job_dead_letter`, `circuit_open`, `write_reconciliation_required` deben investigarse. Reconocer una alerta registra auditoría pero no resuelve el incidente.
- Si se requiere desactivar todo: cambiar `Jobs__Enabled=false`, reiniciar proceso, conservar la BD y registrar el cambio. No ejecutar `DROP TABLE` ni eliminar la cola.
