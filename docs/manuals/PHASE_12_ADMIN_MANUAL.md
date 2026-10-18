# Manual funcional del administrador — Dhole Dynamics Connector

El conector tiene una consola Vue independiente; no está alojada en DholeSystem ni en Pricing. Utilice únicamente el FQDN HTTPS del entorno correcto; jamás cargue tokens JWT o credenciales en capturas o tickets.

## Ingreso y roles

Acceda a `https://<conector>/auth/login` mediante Microsoft Entra. La sesión OIDC tiene cookie segura y anti-CSRF. `dynamics.admin` permite vistas operativas y metadata; `dynamics.security.admin` permite grants, operaciones peligrosas, políticas de acceso y conciliaciones. Si no hay proveedor OIDC configurado, login permanece 503, **no hay cuenta local**.

## Procedimiento diario en la web

1. **Administración**: registre o compruebe la conexión dedicada de Dataverse (organización, tenant y app user); ingrese el secreto solo en el formulario. La UI no permite recuperarlo. Pulse probar conexión sin habilitar escrituras.
2. **Metadatos**: descubrir entidades/campos, revisar el snapshot y su diff, habilitar únicamente las columnas y operaciones requeridas. Comprobar Choice, lookups y el tipo de cada campo.
3. **Permisos**: registrar cliente OAuth M2M con issuer confiable y app ID; definir grants por conexión, entidad, operación, campo y cuota. No usar campos con wildcard ni habilitar permisos de DELETE por defecto.
4. **Consultas guardadas/Buscar**: ejecutar la búsqueda permitida; avanzar por cursor protegido. Si exporta CSV, revisar la finalidad y la retención.
5. **Editor de registros**: crear/actualizar solo con rol SecurityAdmin y autorización. Para actualizar cargar el ETag; en DELETE verificar retención, motivo, confirmación y consentimiento. En producción permanecerá bloqueado hasta aprobación externa de cambios y otra de borrado.
6. **Plantillas**: redactar versiones, preview, publicar únicamente mapeos aprobados. CreateQuote requiere vínculo estable al DholeRateId e idempotencia y no puede duplicar una cotización ante respuesta ambigua.
7. **Jobs**: habilitar sincronización por entidad sólo tras aprobación; revisar checkpoints, circuitos, cola y dead-letters. El replay exige evidencia escrita y rol SecurityAdmin. Nunca repetir una escritura con resultado desconocido.
8. **Auditoría y diagnóstico**: exportar únicamente eventos saneados, revisar métricas 24h, estado de llaves/respaldos mediante monitoreo externo. No insertar credenciales en comentarios o URLs.

## Incidentes frecuentes

- **OIDC 401/403**: comprobar registro de app/issuer, roles de Entra, expiración de cookie y grants; no ampliar permisos a ciegas.
- **Dataverse 429**: ver cola y `Retry-After`; esperar backoff, no forzar POST.
- **Quote UNKNOWN**: verificar la oferta en Dataverse externamente, usar la conciliación administrada; jamás enviar nuevo POST con otra key.
- **Sin filas en búsqueda**: revisar configuración entity/field grants y snapshot, no saltar las políticas.
- **Worker dead-letter**: investigar causa de seguridad/identidad/red, documentar evidencia, replay solamente de job idempotente autorizado.
- **Problemas de TLS**: validar DNS, ACME, reloj y firewall en el servidor; jamás desactivar validación del certificado.

Soporte debe seguir [operación y SLO](../runbooks/PHASE_12_OPERATIONS.md) y [staging E2E](../runbooks/PHASE_12_STAGING_E2E.md) para escalar.
