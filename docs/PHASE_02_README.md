# Fase 02 — persistencia autónoma, secretos y recuperación

## Alcance
- Base de datos lógica dedicada: `dhole_dynamics_connector` (se conserva el esquema `dynamics` para no romper enlaces existentes). El cluster PostgreSQL **puede** ser compartido físicamente, pero en producción deberá utilizar credenciales, permisos, backups y políticas de red independientes.
- Migraciones versionadas `sql/001_init.sql` y `002_secret_rotation.sql` ejecutadas por `Dhole.Dynamics.Tools migrate` bajo un lock advisory y una única transacción, con `SHA-256` que detecta ediciones de migraciones ya aplicadas. El bootstrap 001 es idempotente y no borra datos antiguos; 002 agrega exclusivamente auditoría de rotaciones.
- Cifrado AES-256-GCM con AAD por conexión y versión. Admite `DYNAMICS_ENCRYPTION_KEY_BASE64` de F01 como **clave 1** y nueva clave `DYNAMICS_ENCRYPTION_KEYS_JSON` (formato `{"2":"<BASE64_32_BYTES>"}`) y `DYNAMICS_ENCRYPTION_ACTIVE_KEY_VERSION=2`. Claves externas, nunca en SQL ni Git.
- `Dhole.Dynamics.Tools rotate-keys --dry-run` por defecto. `--apply` requiere `DYNAMICS_ROTATION_APPROVED=YES` **y backup verificado**. Reencripta por registro en transacciones individuales, deja auditoría sin valor sensible y es reanudable ante fallo.
- Copias sin plaintext temporal: `pg_dump -Fc | age`, archivo `.dump.age` con permisos 0600. Restauración **solo** a bases vacías nombradas `dhole_dynamics_connector_restore_*`, con bandera explícita y sin sobrescribir datos.
- Las claves de encriptación de registros NO forman parte de las copias PostgreSQL. Guardarlas en un gestor separado con política de recuperación documentada y acceso restringido.

## Operación segura
1. Aprovisionar base: `DB_NAME=dhole_dynamics_connector bash scripts/provision-database.sh` con credenciales de administración de Postgres solo para el bootstrap. **No ejecutar** contra las bases de Dhole.
2. Ejecutar migraciones con connection string autónoma vía `ConnectionStrings__Dynamics` y `dotnet run --project src/Dhole.Dynamics.Tools -- migrate`. Replay debe reportar éxito sin duplicar filas del ledger.
3. Para backup cifrado, provisionar `age`, `pg_dump`, `AGE_RECIPIENT`, credenciales Postgres en ambiente seguro, `BACKUP_FILE=/ruta/segura/backup-UTC.dump.age` y `bash scripts/backup-encrypted.sh`. No almacenar clave privada age en el mismo host/volumen del backup.
4. Crear una DB vacía de sandbox, nombrada `dhole_dynamics_connector_restore_<id>` y ejecutar `ALLOW_SANDBOX_RESTORE=YES RESTORE_DB_NAME=... AGE_IDENTITY_FILE=... BACKUP_FILE=... bash scripts/restore-sandbox.sh`. Verificar tablas, esquema y que los secretos cifrados se descifran *con una clave versionada recuperada por canal seguro*.
5. Antes de rotar: backup, prueba de restore y copia segura de cada clave antigua; luego agregar clave 2 sin retirar 1, revisar `rotate-keys --dry-run`, y ejecutar `DYNAMICS_ROTATION_APPROVED=YES rotate-keys --apply`. Verificar que no queden filas de versión 1, conciliar resultados y recién entonces definir proceso de retiro de claves antiguas; backups históricos aún pueden requerirlas.
6. Monitorizar pérdida de claves, cambios en roles y eventos de auditoría. Nunca imprimir secretos en stdout ni registrar connection strings.

## Límites y aceptación
- No incluye Fase 03 (autenticación M2M/OIDC, autorización granular), metadatos, motor CRUD o UI de conexiones. Los endpoints de cotizaciones existentes permanecen transicionales.
- El entorno Compose es **exclusivamente local**: PostgreSQL publica solo `127.0.0.1:55432` para ensayos y aquí usa un usuario administrador de desarrollo. No trasladar esta configuración a staging/producción.
- El CI debe demostrar migración inicial y repetida, auditoría de secretos, rotación V1→V2 sin pérdida de plaintext en memoria de salida, y backup/restore cifrado en sandbox. **No declarar F02 cerrada hasta esos jobs aprobados en master y develop.**
- La aprobación humana del modelo de seguridad y revocación de los secretos expuestos históricamente sigue pendiente; este cambio no revoca credenciales en Microsoft Entra.
