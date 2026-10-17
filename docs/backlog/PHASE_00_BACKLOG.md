# Backlog trazable por operación — Fase 00

**Fuente única:** [LEGACY_COVERAGE.csv](../migration/LEGACY_COVERAGE.csv) asigna `LEG-001..LEG-088` a un destino, sus columnas y parámetros. Ninguna operación se marca implementada.

## Gates de trabajo
| Ticket | Alcance | Dependencia | Criterio |
|---|---|---|---|
| F00-SEC-01 | Rotar/revocar credenciales históricas y escanear Git | Owner Entra + legado | Evidencia saneada de revocación y auditoría |
| F00-ARCH-01 | Aprobar ADR-0001…0007 | Arquitectura + seguridad | Revisores registran decisión y riesgos |
| F00-MIG-01 | Confirmar 88 operaciones legacy y mapeos de negocio | Owner legado | 88/88 revisados por negocio y migración |
| F00-INT-01 | Confirmar interfaces ejecutables de consumidores | Owner Dhole/Pricing | HTTP/M2M y IDs, sin dependencia Gateway |
| F01-BASE-01 | API .NET 10 modular y UI autónoma | Gate F00 | Build/test/frontend/docker CI |
| F02-DB-01 | Persistencia aislada, criptografía y backups | F01 | Restore/migraciones y key rotation |
| F03-AUTH-01 | OIDC Admin, OAuth M2M y ABAC | F02 | Denegación cruzada por conexión |
| F04-META-01 | Descubrimiento y snapshots | F03 | Nuevas entidades sin recompilar |
| F05-READ-01 | Search/GET seguro con paginación | F04 | Queries parametrizadas completas |
| F06-WRITE-01 | CRUD/ETags/batch/idempotencia | F05 | Ambigüedad resuelta sin duplicados |
| F07-CONFIG-01 | Templates y mappings | F06 | Operación configurada sin C# |
| F08-UI-01 | Consola Admin completa | F03–F07 | Sin mocks de producción |
| F09-LEG-001…088 | Paridad legacy por ruta de matriz | F07–F08 | 88/88 casos probados o incompatibilidad documentada |
| F10-EXT-01 | Cliente HTTP Dhole/Pricing y CreateQuote | F09 | Sin dependencia DholeApiGateway |
| F11-JOBS-01 | Reintentos, sync delta y recuperación | F10 | Reinicios sin duplicados |
| F12-GOLIVE-01 | Staging, E2E, backups y go-live | Todas | Aprobación expresa antes de producción |

## Regla para cada `F09-LEG-NNN`
1. Resolver `entityName`, todos los campos `$select` y condiciones del CSV contra metadatos reales.
2. Registrar filtros, Choices y proyecciones semánticamente equivalentes; parametrizar inputs y pruebas de inyección.
3. Restringir permisos, paginación, tipos, máximo de filas y scopes.
4. Escribir pruebas de paridad y documentar resultado (incluyendo errores, conjuntos vacíos y múltiples páginas).
5. Cambiar estado de `Planificada` a `Validada` solo con enlace a tests; de lo contrario `Bloqueada` con evidencia. No descartar sin ADR.

**No avanzar a F01** mientras los tres gates críticos de F00 (seguridad, ADRs e inventario revisado) estén pendientes.
