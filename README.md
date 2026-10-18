# DholeDynamicsConnectorService

Servicio independiente para integrar Dynamics 365 / Dataverse. **Fases 01 y 02 validadas; Fase 03 implementada para validación de identidad y permisos.** No es un despliegue a producción.

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
- [Modelo de amenazas](docs/security/THREAT_MODEL.md)
- [CI Fase 02 — master](https://github.com/CustomCodeCR/DholeDynamicsConnectorService/actions/runs/39063682728) · [develop](https://github.com/CustomCodeCR/DholeDynamicsConnectorService/actions/runs/39063679477)

Las fases anteriores tienen CI aprobado. Fase 03 agrega una consola administrativa OIDC, registro de consumidores OAuth y autorización mínima por organización, operación y campos; las pruebas de CI y la validación externa IAM/Dataverse deben completarse antes del uso en producción. Fase 04 implementa el catálogo dinámico y la aprobación explícita de entidades/campos; no se inició Fase 05 ni se desplegó. Antes de conectar información real se deben aprobar controles pendientes de F00, especialmente la **revocación de credenciales históricas** del repositorio legacy; no se han usado ni revocado desde aquí.
