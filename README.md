# DholeDynamicsConnectorService

Servicio independiente para integrar Dynamics 365 / Dataverse. **Fases 01–08 implementadas; validación CI del último commit y pruebas externas IAM/Dataverse son controles separados.** No es un despliegue a producción.

## Capas
La solución .NET 10 separa `Api`, `Application`, `Domain`, `Contracts`, `Infrastructure`, `Persistence`, `Workers` y las herramientas de mantenimiento `Dhole.Dynamics.Tools`. El dashboard Vue 3 + TypeScript permite consultar el estado real del servicio sin simular funciones administrativas.

## Persistencia y secretos (Fase 02)
- Base lógica autónoma `dhole_dynamics_connector`, esquema conservado `dynamics`, migraciones 001/002 serializadas y verificadas por checksum.
- `migrate` y `rotate-keys --dry-run|--apply` disponibles en la CLI de mantenimiento; rotación de AES-256-GCM con llave versionada y lectura de datos V1.
- Backups **cifrados en origen** vía `pg_dump | age`, restauración protegida exclusivamente para una base de pruebas vacía.
- PostgreSQL local de Compose, con migración antes del arranque de API y UI. No usar esta configuración local ni sus usuarios administrativos en producción.

## Ejecutar localmente
Se requieren .NET 10, Docker y Node 22/pnpm 10. Crear `.env` privado desde `.env.example` y proporcionar claves generadas localmente. `docker compose up --build -d` arranca Postgres, el migrador, la API (`127.0.0.1:8080`) y Vue (`127.0.0.1:8081`).

```bash
dotnet restore DholeDynamicsConnectorService.slnx
dotnet build DholeDynamicsConnectorService.slnx -c Release --no-restore
dotnet test DholeDynamicsConnectorService.slnx -c Release --no-build
cd web && pnpm install --frozen-lockfile && pnpm lint && pnpm test && pnpm build
```

## Estado y evidencias
- [Fase 00: auditoría y 88 endpoints del legado](docs/PHASE_00_README.md)
- [Fase 01: plataforma .NET/Vue y CI](docs/PHASE_01_README.md)
- [Fase 02: migraciones, secretos, backup/restore y pruebas reales](docs/PHASE_02_README.md)
- [Fase 03: OIDC Admin, OAuth M2M, RBAC, grants y aislamiento por conexión](docs/PHASE_03_README.md)
- [Fase 04: metadatos Dataverse, snapshots, Choice, relaciones y allowlists](docs/PHASE_04_README.md)
- [Fase 05: lectura/búsqueda tipada, paginación y consultas guardadas](docs/PHASE_05_README.md)
- [Fase 06: CRUD, upsert, batch, ETag, retención e idempotencia](docs/PHASE_06_README.md)
- [Fase 07: plantillas y mapeos configurables, preview y rollback](docs/PHASE_07_README.md)
- [Fase 08: consola Vue de administración, búsqueda, editor, jobs, auditoría y configuración](docs/PHASE_08_README.md)
- [Fase 09: inventario y migración declarativa del legado, cobertura y validación](docs/migration/PHASE_09_README.md)
- [Modelo de amenazas](docs/security/THREAT_MODEL.md)
- [CI Fase 09 — master](https://github.com/CustomCodeCR/DholeDynamicsConnectorService/actions/runs/39092263116) · [develop](https://github.com/CustomCodeCR/DholeDynamicsConnectorService/actions/runs/39092266578)

Las fases 01–09 han sido validadas en CI, incluidas pruebas con PostgreSQL real. Fase 09 inventaría las 88 rutas legacy (87 GET, 1 PATCH): 78 candidatos configurables y 10 incompatibilidades de fuente justificadas. Ningún candidato representa paridad Dataverse productiva sin validación de metadatos y grants en staging. Las pruebas del proyecto no sustituyen una prueba E2E con Microsoft Entra y Dataverse en staging ni implican un despliegue productivo. Antes de conectar datos reales, resolver controles pendientes de F00, incluida la **revocación de credenciales históricas** del repositorio legado. No se inició Fase 10.


## Fase 12 — release independiente (sin go-live automático)

La imagen de release utiliza [Compose aislado](compose.release.yaml), [Caddy HTTPS](deploy/Caddyfile), [seguridad de CodeQL + preflight](.github/workflows/release-security.yml) y workflows **manuales** para publicar imágenes y desplegar con revisión. Véase [estado/gates de Fase 12](docs/PHASE_12_README.md), [manual Admin](docs/manuals/PHASE_12_ADMIN_MANUAL.md) y [manual de integración](docs/manuals/PHASE_12_CONSUMER_MANUAL.md). Staging E2E con Microsoft Entra/Dataverse es requisito pendiente hasta validación con credenciales y conexión reales. Producción no se despliega ni habilita escrituras sin autorización explícita.
