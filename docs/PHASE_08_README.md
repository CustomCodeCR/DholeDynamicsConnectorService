# Fase 08 — Consola administrativa Vue 3

## Alcance completado

La interfaz de DynaConnect conecta **API reales**, sin mocks ni tokens de máquina enviados al navegador. Se mantiene el aislamiento entre **cookie OIDC Admin/SecurityAdmin** y **JWT M2M**. La exploración de registros desde Admin se limita al rol `dynamics.security.admin` y a las entidades/campos aprobados en Fase 04. La ejecución M2M conserva sus grants y cuotas independientes.

### 11 pantallas del plan

1. **Inicio (`/`):** health/live y ready reales; recuentos PostgreSQL de conexiones, apps, plantillas, operaciones ambiguas/rechazadas, actividad y cuota **local** M2M; latencia HTTP p95 de la última hora y errores 5xx recientes. **No representa la cuota total del tenant Dataverse**.
2. **Conexiones (`/admin`):** alta y edición mediante nombre/código, URL Dataverse HTTPS, tenant/client id y secreto nuevo, botón WhoAmI y rotación. **Nunca reenvía ni precarga el secreto existente**.
3. **Metadatos (`/metadata`):** catálogo, campos, Choice, relaciones, capacidades, comparación/snapshot y habilitación explícita; refresco manual/opt-in programado.
4. **Permisos (`/permissions`):** alta de aplicaciones OAuth, selección de conexión, entidad, operación, campos autorizados y cuota; listado de grants PostgreSQL. Las funciones SecurityAdmin se ocultan y se rechazan en servidor si falta el rol.
5. **Buscador (`/search`):** entidades aprobadas, columnas seleccionables, filtros tipados, orden, páginas `@odata.nextLink` con cursor protegido, detalles por GUID y exportación CSV limitada a registros/campos recuperados y confirmada explícitamente. El CSV neutraliza fórmulas `= + - @`.
6. **Editor (`/records`):** formulario generado desde los metadatos para Create/PATCH/DELETE con Choices, números, booleanos y lookups. Para actualizar/borrar obliga a cargar ETag de Dataverse; para borrar además se requiere política, confirmación y motivo. Un timeout ambiguo impide reenvíos automáticos desde la pantalla.
7. **Plantillas (`/templates`):** configuración de entidad/operación, inputs, campos, transformaciones, mapping y preview, historial y rollback, import/export JSON sin secretos.
8. **Consultas guardadas (`/saved-queries`):** campos y parámetros tipados con activación explícita.
9. **Jobs (`/jobs`):** historial real de escrituras, duración, estado, operaciones ambiguas, refrescos programados y desencadenamiento manual de refresco de metadatos. La conciliación manual exige rol SecurityAdmin y confirmación sustentada por verificación externa. **No se reenvían POST inciertos**.
10. **Auditoría (`/audit`):** eventos PostgreSQL y solicitudes HTTP saneadas, con correlación, patrón de ruta, estado y latencia; filtros y límites.
11. **Configuración (`/settings`):** idioma, horario de presentación, proveedores configurados, origen público y estado real del scheduler. El estado de respaldos externos se declara como *no supervisado* en lugar de inventar una señal. No se muestran llaves privadas ni secretos.

## Endpoints nuevos

- `GET /api/admin/v1/console/overview`, `/jobs`, `/audit`, `/schedules`, `/observations`, `/diagnostics`: sesión OIDC Admin, rate limit.
- `POST /api/admin/v1/console/records/{entity}/search`, `GET /api/admin/v1/console/records/{entity}/{guid}`: rol SecurityAdmin, comprobación de allowlist actual y firma del cursor vinculada al usuario Admin.
- `POST /api/admin/v1/console/records/{entity}`, `PATCH/DELETE /api/admin/v1/console/records/{entity}/{guid}`: SecurityAdmin + CSRF, habilitación previa de campos, idempotencia durable, concurrencia ETag y retención para Delete.
- Se reutilizan los endpoints administrativos ya existentes de conexiones, metadatos, clientes/grants, consultas, plantillas y conciliación.

**Separación crítica:** el navegador no obtiene bearer M2M ni el client secret almacenado. El Admin utiliza una vía específica de autorización de SecurityAdmin; la ruta M2M sigue exigiendo roles de la aplicación y grants por entidad/operación/campo de F03/F04. El servidor rechaza campos calculados o no aprobados, y nunca acepta OData/URLs libres.

## Observabilidad y privacidad

Migración `008_admin_console.sql`: `dynamics.http_observations` registra solo GUID de correlación, patrón de ruta (nunca path completo ni query), método, estado, duración y timestamp. No guarda IP, User-Agent, access tokens, body, secretos, cookies ni datos de clientes. El middleware falla sin interrumpir la API si no puede registrar observaciones. El panel consulta a PostgreSQL y no fabrica estados externos.

Un backup con `age` y restauración de sandbox siguen probándose en CI, pero no se presenta un supuesto estado de los backups de producción: ese monitoreo externo está fuera de los endpoints disponibles. Los jobs disponibles son refrescos de metadatos y escrituras/casos de conciliación; no se inventan sincronizadores legados de Fase 09.

## Validación y criterio de aceptación

Un usuario **SecurityAdmin** autorizado puede:
1. Configurar/editar una conexión desde Admin, proporcionar su secreto una sola vez y probar WhoAmI.
2. Refrescar metadatos y habilitar tabla/campos.
3. Registrar una aplicación M2M y crear sus grants, además de configurar búsquedas guardadas.
4. Buscar registros en entidades habilitadas desde la interfaz y exportar únicamente los resultados autorizados.
5. Crear una plantilla versionada, simularla, activarla, ejecutar CRUD mediante permisos y reconciliar una escritura ambigua sin SQL ni modificar el repositorio.

El backend es real y revalida los controles por solicitud. La comprobación completa contra Microsoft Entra y una organización real de Dataverse debe realizarse **con credenciales autorizadas en staging** antes de considerarlo listo para producción; CI utiliza PostgreSQL real y respuestas Dataverse simuladas, no credenciales productivas.

CI incluye pruebas de campos autorizados, ETag, rechazo de escritura, protección frente a consultas anónimas, exportación CSV sin fórmulas, observabilidad saneada, ocho migraciones, retención de secretos, backup cifrado y restauración segura.

**Fase 09 no iniciada.**
