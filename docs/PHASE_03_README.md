# Fase 03 — Identidades independientes, clientes M2M y acceso mínimo a Dataverse

## Alcance implementado

1. **Administrador (BFF independiente).** OAuth/OIDC Authorization Code + PKCE con proveedor externo `AdminOidc`. El servidor obtiene una sesión `__Host-dhole-dynamics-admin` HttpOnly, Secure y SameSite=Lax. No expone access tokens a Vue y no depende de DholeAuth/DholeApiGateway. RBAC de roles `dynamics.admin` y `dynamics.security.admin`. Ausencia de configuración OIDC significa panel bloqueado (401) e inicio de sesión indisponible (503), no acceso de invitado.
2. **M2M.** API JWT Bearer del emisor OAuth externo `M2m:Authority`/audiencia `M2m:Audience`. Validación estándar de firma mediante OpenID Connect metadata/JWKS, issuer, audience, `exp`, app-only identity (`azp`/`appid`/`client_id`) y rol `dynamics.quotes.read` o `dynamics.quotes.create`. Tokens delegados con `scp` no son aceptados como app-only. **JWT simétrico de F01 no se acepta.** No se crea un emisor casero de tokens OAuth.
3. **Registro y acceso:** `dynamics.api_clients` liga un client ID real a emisor validado; `dynamics.api_grants` autoriza por cliente, conexión, entity set, operación y campos exactos. Solo `quotes` y `read/create` están habilitados en F03; el motor genérico sigue reservado para F04–F07. Además del rol del JWT, debe existir un grant habilitado en la base; fallan de forma segura las conexiones distintas, campos no incluidos y clientes deshabilitados. La cuota por grant/minuto se adquiere atómicamente en PostgreSQL y retorna 429 si se agota. Administradores con rol `dynamics.security.admin` pueden gestionar los clientes y permisos.
4. **Conexiones Dataverse:** Admin gestiona organizaciones/códigos independientes y credenciales cifradas existentes de F02. URL restringida a `https://<org>.crm[REGION].dynamics.com` sin puerto no estándar, ruta, credenciales, query ni fragment; no hay rutas de proxy arbitrario. Cliente HTTP deshabilita redirecciones automáticas.
5. **MSAL:** cliente confidencial por conexión/identidad y *generación de secreto*, con caché efímera de 30 minutos y renovación automática por MSAL. Los tokens se usan solo desde backend. El endpoint Admin `POST /api/admin/v1/connections/{code}/test` invoca `WhoAmI()` y prueba **lectura** de `quotes` (`$top=1`) para detectar permisos insuficientes sin escribir datos. No retorna secretos o tokens.
6. **Navegador:** protección antiforgery por cookie y header `X-CSRF-TOKEN` obligatorio en POST/PUT/DELETE de Admin y logout; `/auth/session` autenticado retorna token de un solo origen para usar en memoria. CORS deshabilitado salvo orígenes HTTPS explícitos opcionales; cookies Secure, HttpOnly; no CORS wildcard. Headers nosniff, no-referrer, deny-frames y no-store. Política de limitación para rutas Admin/login.
7. **Seguridad transicional:** los nombres legacy `/api/dynamics/*` siguen enrutados para facilitar la migración, pero **no** aceptan la autenticación previa por llave simétrica: conexiones requieren sesión Admin/CSRF y quotes requieren OAuth M2M y acceso DB. La compatibilidad contractual no debe confundirse con compatibilidad de las credenciales viejas.

## Configuración obligatoria antes de staging/producción

Se necesitan dos **registros de aplicaciones separados** en un proveedor OIDC/OAuth de confianza, más los Application Users de Dataverse con roles mínimos propios. No reutilizar DholeAuth ni credenciales históricas de DynaConnect.

```text
AdminOidc__Authority=https://login.microsoftonline.com/<tenant-guid>/v2.0
AdminOidc__ClientId=<registered-admin-app-id>
AdminOidc__ClientSecret=<from-secret-manager>
Admin__PublicOrigin=https://<public-admin-host>
M2m__Authority=https://login.microsoftonline.com/<tenant-guid>/v2.0
M2m__Audience=api://<resource-application-id>
```

- Registrar en Entra la URI de retorno **HTTPS** `https://<public-admin-host>/signin-oidc`. No confiar en cabeceras `X-Forwarded-*` de clientes arbitrarios. Aplicar TLS al dominio público y configurar el reverse proxy de confianza; la `compose.yaml` HTTP local no sirve para login OIDC con cookies Secure.
- Crear roles de aplicación para Admin: `dynamics.admin`, `dynamics.security.admin`. Asignarlos a usuarios/grupos autorizados. La sesión de Admin no concede un token M2M.
- Exponer el API recurso y registrar roles de aplicación `dynamics.quotes.read`, `dynamics.quotes.create` para consumidores de tipo aplicación. Obtener tokens mediante Client Credentials/.`default` del proveedor externo; **no** incluir `ClientSecret` de Dataverse en una credencial de cliente M2M.
- Dar a cada Microsoft Dataverse Application User los permisos específicos de sus tareas; comprobar manualmente roles en el entorno y ejecutar el test connection: `WhoAmI` correcto no demuestra por sí solo todos los privilegios de escritura. `CanReadQuotes` únicamente indica un probe de lectura.
- En PostgreSQL el esquema se lleva a **migración 003**; la base no almacena credenciales OAuth M2M ni tokens. Preparar backup cifrado y probar restore de F02 antes de aplicar migración en un entorno persistente.

## Contratos relevantes

`GET /auth/session`: sesión autenticada, actor, rol y token antiforgery; `GET /auth/login`: iniciar OIDC; `POST /auth/logout`: invalidar cookie local (con antiforgery).

Administración (requiere cookie Admin, roles y CSRF en escrituras): `GET/POST /api/admin/v1/connections`, `PUT /api/admin/v1/connections/{code}/secret`, `POST /api/admin/v1/connections/{code}/test`, `GET/POST /api/admin/v1/clients`, `GET/POST /api/admin/v1/grants`. Estos endpoints no aceptan tokens M2M.

Runtime (requiere JWT externo app-only, roles de token, registro DB, grant y cuota): `POST /api/v1/quotes`, `GET /api/v1/quotes/{id}?connectionCode=<code>` y `GET /api/v1/quotes/links/{rateId}?connectionCode=<code>`. Estos endpoints no aceptan cookies Admin ni aceptan tokens de login.

## Criterios de aceptación y pruebas

- Tests unitarios de scope, RBAC, campo no autorizado, wildcard prohibido, cuotas válidas, host allowlist y los anteriores de cifrado.
- Test de integración real con PostgreSQL: dos conexiones distintas, un único cliente registrado y grant de campos en una sola organización; verifica permiso correcto, rechazo de otra organización, cliente/operación/campo distintos, cuota 429 y revocación del cliente.
- Tests de HTTP: anónimo rechazado en Admin y M2M, login no configurado rechazado, health sin credenciales. CI de .NET, Vue, auditoría dependencias, gitleaks, Docker/backup.
- **Pendiente fuera de CI:** prueba end-to-end con un proveedor OIDC/Entra real, token OAuth app-only real, Application User y roles reales en Dataverse, verificación de TLS del ingress y revocación de secretos históricos F00. Los tests CI no reemplazan esos controles. No declarar habilitación de producción hasta obtener evidencia del responsable IAM/Dataverse.

## Regla de fases

No se activa metadata discovery, CRUD genérico, UI de editor arbitrario, sincronización de datos o envío a otros repositorios: pertenecen a F04+. No iniciar F04 automáticamente.
