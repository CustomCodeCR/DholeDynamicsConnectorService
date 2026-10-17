# Inventario verificable del legado DynaConnect — Fase 00

> **Estado:** catálogo auditado en el código fuente; ninguna operación fue migrada ni verificada contra una organización Dynamics real. No contiene secretos.

**Origen:** [MauLang18/DynaConnect/DynamicsCF/DynamicsCF/Controllers/ValuesController.cs](https://github.com/MauLang18/DynaConnect/blob/master/DynamicsCF/DynamicsCF/Controllers/ValuesController.cs) (rama `master`).
**Extracción:** atributos ASP.NET `[HttpGet]` / `[HttpPatch]`, `[Route]`, variable `entityName`, `$select`, campos de `$filter` y parámetros formales. Esta clasificación no equivale a paridad funcional.

**Recuento auditable:** 88 rutas, 87 GET y 1 PATCH. Los IDs `LEG-001` … `LEG-088` también están en [LEGACY_COVERAGE.csv](LEGACY_COVERAGE.csv).

## Criterios de destino

- **Consulta guardada parametrizada:** búsqueda por columnas permitidas, filtros tipados, límites y paginación; muchos GET que hoy construyen OData se consolidan sin controladores por endpoint.
- **Lectura por identificador autorizado:** GET one con validación de GUID, entidad y permisos; confirmar en Fase 09 cómo gestionar la semántica de filtros y el contrato anterior.
- **Plantilla de UPDATE:** el único PATCH, `/dynamics/UpdateDynamics`, actualiza BCF en `incidents`; exigir política de escritura, ETag, auditoría, validación de campos y autorización explícita.
- **Metadatos por confirmar:** `incidents`, `accounts`, `quotes`, `competitors`, `salesliteratures` son nombres extraídos de `entityName` legado, **no** una garantía de entity set o de permisos en la organización futura.
- **Campos por confirmar:** los campos y Choice literales extraídos del controlador son solo evidencia del legado. Validar existencia, tipos, escritura, estado, vistas, opciones y nombres lógicos contra Metadatos Dataverse.

## Matriz endpoint → destino

| ID | Método y ruta | Familia | Entidad fuente | Destino propuesto |
|---|---|---|---|---|
| LEG-001 | `GET /dynamics/listarId` | Tracking y búsqueda por ID/PO/BCF/contenedor | `incidents` | consulta guardada parametrizada |
| LEG-002 | `GET /dynamics/listarPo` | Tracking y búsqueda por ID/PO/BCF/contenedor | `incidents` | consulta guardada parametrizada |
| LEG-003 | `GET /dynamics/listarBcf` | Tracking y búsqueda por ID/PO/BCF/contenedor | `incidents` | consulta guardada parametrizada |
| LEG-004 | `GET /dynamics/listarContenedor` | Tracking y búsqueda por ID/PO/BCF/contenedor | `incidents` | consulta guardada parametrizada |
| LEG-005 | `GET /dynamics/listarClienteF` | Clientes y trámites | `incidents` | consulta guardada parametrizada |
| LEG-006 | `GET /dynamics/listarClienteA` | Clientes y trámites | `incidents` | consulta guardada parametrizada |
| LEG-007 | `GET /dynamics/listarClienteNombre` | Clientes y trámites | `accounts` | lectura por identificador autorizado |
| LEG-008 | `GET /dynamics/listarClienteFiltroA` | Clientes y trámites | `incidents` | consulta guardada parametrizada |
| LEG-009 | `GET /dynamics/listarClienteFiltroF` | Clientes y trámites | `incidents` | consulta guardada parametrizada |
| LEG-010 | `GET /dynamics/listarClienteIdtra` | Clientes y trámites | `incidents` | consulta guardada parametrizada |
| LEG-011 | `GET /dynamics/listarClienteBcf` | Clientes y trámites | `incidents` | consulta guardada parametrizada |
| LEG-012 | `GET /dynamics/listarClienteContenedor` | Clientes y trámites | `incidents` | consulta guardada parametrizada |
| LEG-013 | `GET /dynamics/listarClientePo` | Clientes y trámites | `incidents` | consulta guardada parametrizada |
| LEG-014 | `GET /dynamics/listarEjecutivoFiltroA` | Ejecutivos | `incidents` | consulta guardada parametrizada |
| LEG-015 | `GET /dynamics/listarEjecutivoFiltroF` | Ejecutivos | `incidents` | consulta guardada parametrizada |
| LEG-016 | `GET /dynamics/listarEjecutivoIdtra` | Ejecutivos | `incidents` | consulta guardada parametrizada |
| LEG-017 | `GET /dynamics/listarEjecutivobCF` | Ejecutivos | `incidents` | consulta guardada parametrizada |
| LEG-018 | `GET /dynamics/listarEjecutivoContenedor` | Ejecutivos | `incidents` | consulta guardada parametrizada |
| LEG-019 | `GET /dynamics/listarEjecutivoPo` | Ejecutivos | `incidents` | consulta guardada parametrizada |
| LEG-020 | `GET /dynamics/VistaGeneral` | Detalle, contenedores y precios | `incidents` | consulta guardada parametrizada |
| LEG-021 | `GET /dynamics/Contenedores` | Detalle, contenedores y precios | `incidents` | consulta guardada parametrizada |
| LEG-022 | `GET /dynamics/Precios` | Detalle, contenedores y precios | `incidents` | consulta guardada parametrizada |
| LEG-023 | `GET /dynamics/listarRJIdtra` | Trámites RJ | `incidents` | consulta guardada parametrizada |
| LEG-024 | `GET /dynamics/listarRJBcf` | Trámites RJ | `incidents` | consulta guardada parametrizada |
| LEG-025 | `GET /dynamics/listarRJContenedor` | Trámites RJ | `incidents` | consulta guardada parametrizada |
| LEG-026 | `GET /dynamics/listarRJPo` | Trámites RJ | `incidents` | consulta guardada parametrizada |
| LEG-027 | `GET /dynamics/listarFiltroA` | Trámites RJ | `incidents` | consulta guardada parametrizada |
| LEG-028 | `GET /dynamics/VistaGeneral2` | Detalle, contenedores y precios | `incidents` | consulta guardada parametrizada |
| LEG-029 | `GET /dynamics/Contenedores2` | Detalle, contenedores y precios | `incidents` | consulta guardada parametrizada |
| LEG-030 | `GET /dynamics/Precios2` | Detalle, contenedores y precios | `incidents` | consulta guardada parametrizada |
| LEG-031 | `GET /dynamics/listarCotizacionesS` | Cotizaciones y casos | `quotes` | consulta guardada parametrizada |
| LEG-032 | `GET /dynamics/listarCotizacionesA` | Cotizaciones y casos | `quotes` | consulta guardada parametrizada |
| LEG-033 | `GET /dynamics/listarCotizacionesRJS` | Cotizaciones y casos | `quotes` | consulta guardada parametrizada |
| LEG-034 | `GET /dynamics/listarCotizacionesRJA` | Cotizaciones y casos | `quotes` | consulta guardada parametrizada |
| LEG-035 | `GET /dynamics/listarCaso` | Cotizaciones y casos | `incidents` | lectura por identificador autorizado |
| LEG-036 | `GET /dynamics/listarTarifasMercadoS` | Cotizaciones y casos | `quotes` | consulta guardada parametrizada |
| LEG-037 | `GET /dynamics/TramitesOrigenSS` | Trámites por origen | `incidents` | consulta guardada parametrizada |
| LEG-038 | `GET /dynamics/TramitesOrigenAM` | Trámites por origen | `incidents` | consulta guardada parametrizada |
| LEG-039 | `GET /dynamics/TramitesOrigenDS` | Trámites por origen | `incidents` | consulta guardada parametrizada |
| LEG-040 | `GET /dynamics/TramitesOrigenAH` | Trámites por origen | `incidents` | consulta guardada parametrizada |
| LEG-041 | `GET /dynamics/listarCertificadosA` | Certificados | `incidents` | consulta guardada parametrizada |
| LEG-042 | `GET /dynamics/listarCertificadosARJ` | Certificados | `incidents` | consulta guardada parametrizada |
| LEG-043 | `GET /dynamics/listarCertificadosF` | Certificados | `incidents` | consulta guardada parametrizada |
| LEG-044 | `GET /dynamics/listarCertificadosFactura` | Certificados | `incidents` | consulta guardada parametrizada |
| LEG-045 | `GET /dynamics/listarCertificadosIdtra` | Certificados | `incidents` | consulta guardada parametrizada |
| LEG-046 | `GET /dynamics/DatosCertificados` | Certificados | `incidents` | consulta guardada parametrizada |
| LEG-047 | `GET /dynamics/ColocacionesPtoAlmacen` | Movimientos portuarios | `incidents` | consulta guardada parametrizada |
| LEG-048 | `GET /dynamics/MovimientoPanamaCrc` | Movimientos portuarios | `incidents` | consulta guardada parametrizada |
| LEG-049 | `GET /dynamics/AlmacenFiscalIdtra` | Almacén fiscal | `incidents` | consulta guardada parametrizada |
| LEG-050 | `GET /dynamics/AlmacenFiscalPo` | Almacén fiscal | `incidents` | consulta guardada parametrizada |
| LEG-051 | `GET /dynamics/AlmacenFiscalFactura` | Almacén fiscal | `incidents` | consulta guardada parametrizada |
| LEG-052 | `GET /dynamics/AlmacenFiscalBcf` | Almacén fiscal | `incidents` | consulta guardada parametrizada |
| LEG-053 | `GET /dynamics/AlmacenFiscalContenedor` | Almacén fiscal | `incidents` | consulta guardada parametrizada |
| LEG-054 | `GET /dynamics/WHSMiamiPreAlerta` | Almacén Miami | `incidents` | consulta guardada parametrizada |
| LEG-055 | `GET /dynamics/WHSMiamiBodega` | Almacén Miami | `incidents` | consulta guardada parametrizada |
| LEG-056 | `GET /dynamics/WHSMiamiDespacho` | Almacén Miami | `incidents` | consulta guardada parametrizada |
| LEG-057 | `GET /dynamics/WHSMiamiBcf` | Almacén Miami | `incidents` | consulta guardada parametrizada |
| LEG-058 | `GET /dynamics/WHSMiamiContenedor` | Almacén Miami | `incidents` | consulta guardada parametrizada |
| LEG-059 | `GET /dynamics/WHSMiamiTracking` | Almacén Miami | `incidents` | consulta guardada parametrizada |
| LEG-060 | `GET /dynamics/WHSMiamiIdtra` | Almacén Miami | `incidents` | consulta guardada parametrizada |
| LEG-061 | `GET /dynamics/WHSMiamiFactura` | Almacén Miami | `incidents` | consulta guardada parametrizada |
| LEG-062 | `GET /dynamics/WHSMiamiPo` | Almacén Miami | `incidents` | consulta guardada parametrizada |
| LEG-063 | `GET /dynamics/WHSMiamiWhs` | Almacén Miami | `incidents` | consulta guardada parametrizada |
| LEG-064 | `GET /dynamics/WHSPanamaTransito` | Almacén Panamá | `incidents` | consulta guardada parametrizada |
| LEG-065 | `GET /dynamics/WHSPanamaBodega` | Almacén Panamá | `incidents` | consulta guardada parametrizada |
| LEG-066 | `GET /dynamics/WHSPanamaDespacho` | Almacén Panamá | `incidents` | consulta guardada parametrizada |
| LEG-067 | `GET /dynamics/WHSPanamaBcf` | Almacén Panamá | `incidents` | consulta guardada parametrizada |
| LEG-068 | `GET /dynamics/WHSPanamaContenedor` | Almacén Panamá | `incidents` | consulta guardada parametrizada |
| LEG-069 | `GET /dynamics/WHSPanamaTracking` | Almacén Panamá | `incidents` | consulta guardada parametrizada |
| LEG-070 | `GET /dynamics/WHSPanamaIdtra` | Almacén Panamá | `incidents` | consulta guardada parametrizada |
| LEG-071 | `GET /dynamics/WHSPanamaFactura` | Almacén Panamá | `incidents` | consulta guardada parametrizada |
| LEG-072 | `GET /dynamics/WHSPanamaPo` | Almacén Panamá | `incidents` | consulta guardada parametrizada |
| LEG-073 | `GET /dynamics/WHSPanamaWhs` | Almacén Panamá | `incidents` | consulta guardada parametrizada |
| LEG-074 | `PATCH /dynamics/UpdateDynamics` | Actualización BCF | `incidents` | plantilla versionada de UPDATE con permisos de escritura |
| LEG-075 | `GET /dynamics/listarCodigoCaso` | Casos | `incidents` | consulta guardada parametrizada |
| LEG-076 | `GET /dynamics/listarTramitesAduanalesF` | Aduanas | `competitors` | consulta guardada parametrizada |
| LEG-077 | `GET /dynamics/listarTramitesAduanalesA` | Aduanas | `competitors` | consulta guardada parametrizada |
| LEG-078 | `GET /dynamics/listarAduanaIdtra` | Aduanas | `competitors` | consulta guardada parametrizada |
| LEG-079 | `GET /dynamics/listarAduanaRecibo` | Aduanas | `competitors` | consulta guardada parametrizada |
| LEG-080 | `GET /dynamics/listarAduanaSolicitud` | Aduanas | `competitors` | consulta guardada parametrizada |
| LEG-081 | `GET /dynamics/listarAduanaBl` | Aduanas | `competitors` | consulta guardada parametrizada |
| LEG-082 | `GET /dynamics/listarExoneracionesA` | Exoneraciones | `salesliteratures` | consulta guardada parametrizada |
| LEG-083 | `GET /dynamics/listarExoneracionesF` | Exoneraciones | `salesliteratures` | consulta guardada parametrizada |
| LEG-084 | `GET /dynamics/listarExoneracionesIdtra` | Exoneraciones | `salesliteratures` | consulta guardada parametrizada |
| LEG-085 | `GET /dynamics/listarExoneracionesProducto` | Exoneraciones | `salesliteratures` | consulta guardada parametrizada |
| LEG-086 | `GET /dynamics/listarExoneracionesSolicitud` | Exoneraciones | `salesliteratures` | consulta guardada parametrizada |
| LEG-087 | `GET /dynamics/listarExoneracionesAutorizacion` | Exoneraciones | `salesliteratures` | consulta guardada parametrizada |
| LEG-088 | `GET /dynamics/ClienteIdtra` | Clientes / IDTRA | `incidents` | consulta guardada parametrizada |

## Reglas para cierre de migración (Fase 09, no ejecutar ahora)

Cada ID requiere una definición versionada, selección completa, filtros equivalentes con tipos seguros, parámetros obligatorios/opcionales, reglas Choice confirmadas, scopes, pruebas con múltiples registros y paginación. Nunca copiar credenciales del controlador; prohibida la interpolación directa de input en `$filter`. La matriz incluye los campos de proyección extraídos y las columnas de filtrado identificables por análisis estático.

**No descartado:** los 88 quedan asignados a un destino. Ninguna eliminación está autorizada antes de demostrar duplicidad o inutilidad y registrar ADR con evidencia. Las categorías son taxonomía funcional, no entidades Dynamics.
