# Fase 01 — Base compilable y autónoma

## Alcance implementado en código
- Solución .NET 10: Api, Application, Contracts, Domain, Infrastructure, Persistence, Workers.
- Repositorios SQL y cifrado trasladados conservando el esquema actual; no ejecutar migraciones ni escrituras en entornos reales.
- Orquestación de creación de Quotes separada de `Program.cs`. Los endpoints existentes `/api/dynamics/*` permanecen y se añade `/api/v1/quotes` y `/api/admin/v1/connections` como contratos **transicionales**, no motor genérico.
- Health live y ready, correlation id, ProblemDetails, handler de errores seguro, OpenAPI en desarrollo (documentos `v1` y `admin-v1`).
- Vue 3 + TS + Pinia + vue-router + Tailwind + vue-i18n ES/EN, responsive. Dashboard consume de verdad `/health/live` y `/health/ready`; no muestra mocks ni simula acceso administrativo.
- Docker multistage, Compose **solo local** con PostgreSQL dedicado, API y UI; CI para dotnet, pnpm, scans y smoke.
- Tests: validación URL, estados Quote e idempotencia, autenticación anónima, correlación, readiness con DB no disponible y health store.

## Desarrollo local
Requiere .NET 10 SDK, Node 22, pnpm 10, Docker Compose para smoke.

```bash
dotnet restore DholeDynamicsConnectorService.slnx
dotnet build DholeDynamicsConnectorService.slnx -c Release --no-restore
dotnet test DholeDynamicsConnectorService.slnx -c Release --no-build

cd web
pnpm install --frozen-lockfile
pnpm lint && pnpm test && pnpm build
```

Para Compose, crear `.env` local desde `.env.example` **sin secretos de producción**:
- `POSTGRES_PASSWORD`: valor aleatorio largo.
- `JWT_SECRET_KEY`: al menos 32 bytes; para local se puede generar con `openssl rand -hex 32`.
- `DYNAMICS_ENCRYPTION_KEY_BASE64`: 32 bytes aleatorios codificados Base64; `openssl rand -base64 32`.
- `docker compose up --build -d`. Admin: http://127.0.0.1:8081; API: http://127.0.0.1:8080.
- `docker compose down` preserva DB de desarrollo; `docker compose down -v` **elimina datos locales** y no debe usarse con información real.

## Seguridad y límites
- **No desplegar producción:** Compose y JWT simétrico del prototipo son transicionales. OAuth2 M2M y OIDC autónomos, política de columnas y conexiones, migraciones EF, secret rotation son fases 02–03.
- No hay UI CRUD ni acceso a Dataverse desde el navegador.
- Los valores Choice y `quotes` en el cliente existente siguen siendo prototipo pendiente de migrar a configuración en las fases 04–07.
- Las credenciales comprometidas del legado necesitan revocación y evidencia del dueño de Entra conforme a F00.
- `Workers` es un proyecto reservado, no ejecuta sincronizaciones ni jobs hasta la fase 11.
- Se versiona `web/pnpm-lock.yaml` generado mediante pnpm 10.14.0; CI y Docker usan `--frozen-lockfile`.

## Evidencia de verificación — 2026-10-17
CI ejecutada en GitHub Actions con aprobación de los cuatro jobs en **ambas ramas** para el commit `eb5343167856bdbbad835980bea12a8b4a189ebf`:
- [Develop — ejecución validada](https://github.com/CustomCodeCR/DholeDynamicsConnectorService/actions/runs/39062537527)
- [Master — ejecución validada](https://github.com/CustomCodeCR/DholeDynamicsConnectorService/actions/runs/39062540201)
- **.NET 10:** restore y build exitosos; 10 unit tests y 3 integration smoke tests aprobados; no se informaron vulnerabilidades NuGet en el proyecto API.
- **Frontend:** `pnpm install --frozen-lockfile`, eslint, 2 tests Vitest, `pnpm build` y auditoría de dependencias de producción exitosas.
- **Seguridad:** escaneo gitleaks del árbol versionado exitoso. No sustituye una revisión de secretos históricos/rotación de Fase 00.
- **Docker:** compilación de imágenes API/Frontend; `docker compose up` local con PostgreSQL y verificaciones HTTP `/health/ready` y UI aprobadas. Los volúmenes fueron descartados al finalizar CI.

**Fase 01 — criterios técnicos de aceptación verificados.** No desplegado a staging ni producción; pruebas Dataverse reales no forman parte de Fase 01. No iniciar F02 automáticamente. Siguen abiertos los controles organizativos de revocación de credenciales históricas y aprobación arquitectónica documentados en Fase 00.
