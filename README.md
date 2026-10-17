# DholeDynamicsConnectorService

Plataforma de integración independiente con Dynamics 365 / Microsoft Dataverse. **Fase 01 validada:** solución modular .NET 10, frontend Vue 3 de administración, tests automatizados y Docker Compose local. El producto aún no implementa el motor CRUD genérico ni las funciones completas de administración.

## Arquitectura

`Dhole.Dynamics.Api` expone los health checks y los contratos preliminares `/api/v1` (runtime) y `/api/admin/v1` (administración). Las capas `Application`, `Domain`, `Contracts`, `Infrastructure` y `Persistence` aíslan la lógica de los adaptadores HTTP y PostgreSQL. `Workers` está reservado para las fases futuras.

El frontend `web/` utiliza Vue 3, TypeScript, Pinia, Tailwind, Vue Router e i18n español/inglés. Muestra health checks reales y deja explícitamente pendientes las pantallas administrativas que dependen de fases posteriores.

**El conector NO depende de DholeApiGateway ni de DholeAuth.** DholeSystem será un consumidor HTTP externo con permisos propios cuando se implemente la autenticación M2M de Fase 03.

## Comprobar

```bash
dotnet restore DholeDynamicsConnectorService.slnx
dotnet build DholeDynamicsConnectorService.slnx -c Release --no-restore
dotnet test DholeDynamicsConnectorService.slnx -c Release --no-build

cd web
corepack enable
pnpm install --frozen-lockfile
pnpm lint && pnpm test && pnpm build
```

Para ejecutar API, base de datos y frontend localmente, configurar `.env` a partir de `.env.example` con credenciales **aleatorias de desarrollo** y ejecutar `docker compose up --build -d` (UI `127.0.0.1:8081`, API `127.0.0.1:8080`). No usar secretos, credenciales ni datos de producción.

## Documentación y estado real

- [Fase 00 — auditoría, ADRs y riesgos](docs/PHASE_00_README.md)
- [Fase 01 — guía, límites, pruebas y evidencia CI](docs/PHASE_01_README.md)
- [Modelo de amenazas](docs/security/THREAT_MODEL.md)
- [Inventario de 88 operaciones legacy](docs/migration/LEGACY_INVENTORY.md)

El CI validó build, tests, frontend, dependencias y Docker en `master` y `develop` el **17 de octubre de 2026**. Esto **no** equivale a un despliegue. Autenticación OIDC/M2M independiente, secretos y persistencia definitiva, motor genérico, UI administrativa completa y jobs pertenecen a fases posteriores. No habilitar operaciones de producción hasta cumplir sus controles de seguridad.

**Importante:** existen credenciales históricamente incrustadas en DynaConnect legacy. El procedimiento de revocación está en [rotación del legado](docs/security/LEGACY_SECRET_ROTATION.md), pero su revocación no está confirmada. No reutilizarlas.
