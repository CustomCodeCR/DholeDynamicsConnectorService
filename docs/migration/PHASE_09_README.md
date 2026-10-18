# Fase 09 — Migración funcional auditada de DynaConnect legacy

**Estado de implementación:** paquete versionado de las 88 rutas del controlador `MauLang18/DynaConnect` (commit `e0490c3260372e547f8672974633104268f89233`), compilación de candidatos al DSL seguro de Fase 05, importación idempotente en borrador, validación contra metadatos aprobados y matriz individual. **No se activó ninguna operación por defecto ni se ejecutó Dataverse real.**

## Inventario y cobertura verificable

- **88 rutas:** 87 GET y un PATCH, identificadores `LEG-001` a `LEG-088`.
- **79 GET con traducción candidata** a `ReadSpec` y plantilla `find` parametrizada, limitada a 25 registros por página y máximo 5 páginas (cursor seguro del motor de Fase 05). No hay concatenación de `$filter` ni OData libre.
- **9 rutas bloqueadas por evidencia en el controlador:** LEG-008 y LEG-009 contienen parámetros declarados no usados por sus filtros; LEG-014 tiene una separación `,` inválida en el filtro Choice; LEG-020 y LEG-028 proyectan 31 campos, superando el límite seguro actual de 30; LEG-036 depende de un GUID fijo de otra organización (no se replica); LEG-074 usa PATCH de BCF con `If-Match: *`, reemplazado por contrato con ETag específico; LEG-080 y LEG-081 utilizan expresiones `(campo,'{name}')` sin operador OData válido.
- **0 rutas declaradas paridad productiva:** la validación final contra los metadatos reales, Choices, valores de estado, entidades/relationship names y permisos de la organización destino sigue pendiente. Los 79 candidatos no se activan si faltan campos o difieren sus tipos.
- No se excluyó ninguna ruta por simple semejanza de nombre. Las nueve incompatibilidades tienen causa y línea de origen en la matriz; su remediación necesita corrección explícita y nueva revisión del paquete.

**Matriz de cobertura por ruta:** [PHASE09_COVERAGE.csv](PHASE09_COVERAGE.csv). **Datos fuente:** [LEGACY_COVERAGE.csv](LEGACY_COVERAGE.csv) y [LEGACY_INVENTORY.md](LEGACY_INVENTORY.md). El paquete normaliza nombres de entity set del controlador al nombre lógico de Dataverse: `incidents→incident`, `accounts→account`, `quotes→quote`, `competitors→competitor` y `salesliteratures→salesliterature`. Además, los lookup OData `_campo_value` se materializan exclusivamente desde los atributos Lookup/Customer/Owner que realmente descubra Fase 04, como campos derivados **solo lectura**, cada uno sujeto a autorización independiente. El paquete tipado está en [legacy/phase09-v1.json](../../legacy/phase09-v1.json).

## Familias

Las consultas permanecen clasificadas en tracking/IDTRA/BCF/PO/contenedor; clientes y trámites; ejecutivos; precios y detalle; RJ; cotizaciones/casos; trámites por origen; certificados; movimientos portuarios; almacén fiscal; almacenes Miami/Panamá; aduanas; exoneraciones. Las rutas históricas se conservan **solo en el inventario** para trazar migración: **no se crea un endpoint por cada ruta**.

## Importador y protección

UI Admin: **`/legacy-migration`**. Requiere sesión OIDC; solo **SecurityAdmin** puede importar mediante confirmación, CSRF y el botón explícito.

API Admin protegida:
- `GET /api/admin/v1/connections/{code}/legacy-migration/inventory` lista las 88 correspondencias y bloqueos de origen.
- `GET /api/admin/v1/connections/{code}/legacy-migration/preview` evalúa **sin cambios** el catálogo/allowlist actual, filtros, campos, operaciones y conflictos de nombre.
- `POST /api/admin/v1/connections/{code}/legacy-migration/import` con `{"confirm":true}` crea únicamente consultas y plantillas **desactivadas** compatibles; no modifica definiciones preexistentes diferentes ni asigna permisos M2M.
- `GET /api/admin/v1/connections/{code}/legacy-migration/history` consulta el resultado por ruta guardado en `dynamics.legacy_migration_items` (migración 009, sin información secreta). Cada importación queda auditada.

Los estados posibles son `blocked_source`, `needs_configuration`, `name_conflict`, `ready_to_stage`, `staged` y `already_staged`. El proceso es repetible, y la habilitación de una consulta y la activación de una plantilla son pasos separados y manuales. La identidad de la conexión sale de la API, jamás del archivo fuente legado.

### Contrato nuevo por consumidor

`GET /dynamics/listarId?name=ABC` se reemplaza, **tras aprobar metadata y grants**, por:

1. Consulta `legacy-001` con parámetro `name` tipado `string` y `contains(title,@p0)`.
2. `POST /api/v1/saved-queries/legacy-001/execute` con `{"connectionCode":"org","parameters":{"name":"ABC"}}`; o tras activar su plantilla, `POST /api/v1/templates/Legacy001/execute` con `{"connectionCode":"org","inputs":{"name":"ABC"}}`.
3. Seguir el cursor `nextCursor` por la misma API para paginación. El formato de respuesta cambia a `ReadSearchResponse` (items/count/page/nextCursor), no el string JSON del controlador legado. El cliente debe adaptar su serialización.

**LEG-074** (`PATCH /dynamics/UpdateDynamics`): la semántica segura es plantilla `update` sobre `incidents.new_bcf` con `recordId` GUID, `eTag` concreto e `Idempotency-Key`. El PATCH histórico con coincidencia indiscriminada queda incompatible hasta completar la nueva negociación de concurrencia; no se importa automáticamente como si fuera equivalente.

No hay compatibilidad de rutas directas `/dynamics/*` en producción: el cambio de consumidores está previsto para Fase 10, sin adelantarla. No se importaron certificados, secretos, GUID de conexión ni credenciales de la aplicación histórica. El código legado contiene material sensible y necesita completar la rotación/revocación externa ya identificada en Fase 00.

## Procedimiento de promoción por organización

1. Configurar conexión y credenciales **nuevas** mediante Admin/OIDC; comprobar `WhoAmI` y descubrir metadata real.
2. Comparar `preview` con la matriz, corregir campos Choice/lookup ausentes o diferentes y aprobar tablas/campos en Fase 04. No convertir filtros malformados sin un contrato corregido.
3. Importar solo los candidatos validados. Revisar `history` y repetir importación sin perder borradores existentes.
4. Crear grants M2M de lectura por entidad y campos, habilitar cada `saved_query` explícitamente y activar manualmente su `IntegrationTemplate`. No copiar scopes/grants amplios del servicio anterior.
5. En staging, comparar resultados con un dataset sanitizado del legado por ruta (proyección, filtros, estado, nulos, orden), varias páginas y límites; registrar evidencias y corregir diferencias mediante nuevas versiones.
6. Migrar clientes con los contratos estructurados y retirar la autorización antigua **solo** después de comprobar comportamiento e integridad. Esta integración entre repositorios es Fase 10, no incluida aquí.

## Validación automatizada

Las pruebas unitarias recorren 88 entradas, controlan 87 GET/1 PATCH, verifican bloqueos, compilan los 79 DSL candidatos con metadatos sintéticos, prueban parámetros con apóstrofes y preservan el ETag. La prueba PostgreSQL comprueba preview sin efectos, staging desactivado, idempotencia, persistencia de tareas y separación entre conexiones. CI aplica **9 migraciones**, Docker smoke y backup/restauración cifrado.

**Limitación explícita:** el paquete está listo para validación y staging por organización, pero sin acceso a la metadata real no se puede demostrar paridad semántica productiva de las 79 operaciones. No se inicia Fase 10.
