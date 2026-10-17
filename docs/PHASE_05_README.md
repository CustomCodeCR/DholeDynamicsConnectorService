# Fase 05 — Lectura y búsqueda genérica

## Alcance

- Lectura GET por GUID y POST por clave alternativa declarada en metadatos.
- POST search con DSL estructurado: hojas field/op/value, grupos join/children and-or; operadores eq, ne, gt, ge, lt, le, contains, in y null. Valores estrictamente tipados por metadata; codificación OData con aliases de parámetros, nunca filtros OData crudos.
- Select, ordenación, count y expansión de relaciones directas Many-to-One solo con autorización adicional sobre campos de la entidad relacionada. Límites: 30 columnas, 4 ordenamientos, 2 expansiones, 24 predicados, 5 niveles, 15 elementos in, páginas de 1–100 elementos y máximo 5 páginas.
- Paginación con nextLink original de Dataverse (sin $skip); continuación protegida AES-256-GCM, 15 minutos de validez, ligada a identidad M2M, conexión, entidad, versión de snapshot y plantilla guardada. En cada página se vuelven a verificar permisos y cuotas, y el destino queda restringido a HTTPS/443 y el mismo host y entity set.
- PostgreSQL migración 005: consultas guardadas JSONB por conexión, con schema de parámetros tipados, auditoría y estado disabled por defecto. Crear/modificar una plantilla la desactiva y requiere aprobación administrativa expresa. No se aceptan SQL u OData arbitrarios.
- Autorizar cada lectura por la unión de roles OAuth app-only (dynamics.records.read), grant M2M en PostgreSQL para entidad, operación read y campos, y allowlists de tablas y campos del snapshot F04. Filtrar u ordenar por campos deshabilitados también está prohibido.
- Fases 02, 03 y 04 siguen cubiertas por CI, incluidas migraciones, aislamiento M2M, y backup/restore cifrado.

## Rutas M2M (Bearer app-only)

- GET /api/v1/records/{entity}/{id:guid}?connectionCode=ORG&select=new_po,new_status
- POST /api/v1/records/{entity}/by-key : {"connectionCode":"ORG","keys":{"new_po":"PO-123"},"select":["new_po"]}
- POST /api/v1/records/{entity}/search : {"connectionCode":"ORG","query":{"select":["new_po","new_status"],"filter":{"field":"new_po","op":"eq","value":"PO-123"},"pageSize":25,"count":true}}
- Para otra página POST a la misma ruta con {"connectionCode":"ORG","cursor":"token"} y sin query.
- POST /api/v1/saved-queries/{name}/execute : {"connectionCode":"ORG","parameters":{"po":"PO-123"}}. Para páginas siguientes enviar cursor y parameters vacío.

Las respuestas de búsqueda contienen entity, items, nextCursor, count, page, metadataVersion y truncated. La clave alternativa requiere una definición EntityKeyMetadata descubierta en F04.

## Rutas Admin OIDC (cookie y CSRF)

- GET/POST /api/admin/v1/connections/{code}/saved-queries
- PUT /api/admin/v1/connections/{code}/saved-queries/{name}/enabled

Ejemplo para búsquedas IDTRA, BCF, PO, contenedor o cliente: habilitar la tabla y campos correspondientes en F04 y otorgar grant M2M en F03; guardar una plantilla con name shipment_po, entityName new_shipments, query.select ["new_po"], query.filter {"field":"new_po","op":"eq","parameter":"po"} y parameters [{"name":"po","type":"string","required":true}]. No hace falta crear endpoints C# específicos para esas búsquedas. El nombre lógico real depende de la organización.

El parámetro READ_CURSOR_KEY_BASE64 debe tener 32 bytes aleatorios Base64 y venir de un gestor de secretos en staging/producción. Si no se configura en desarrollo, la clave de cursor es efímera y los cursores caducan al reiniciar.

## Pruebas y aceptación

Unit tests: OData seguro, filtros anidados, caracteres especiales, validación de tipos, controles de longitud, cursor caducado/manipulado, host y ruta no autorizados. Integration tests: dos conexiones PostgreSQL, políticas, cuotas por cada página, continuación entre organizaciones, invalidez por cambios de snapshot, y plantillas guardadas deshabilitadas y revalidadas.

Se requiere conexión a una organización de staging Dataverse autorizada para verificar datos de IDTRA/BCF/PO reales, permisos del Application User y comportamiento real del paginado; no se usaron secretos históricos ni se ha desplegado en producción.

No se inició Fase 06.

## Referencias Microsoft

https://learn.microsoft.com/en-us/power-apps/developer/data-platform/webapi/query-data-web-api
https://learn.microsoft.com/en-us/power-apps/developer/data-platform/webapi/query/page-results
