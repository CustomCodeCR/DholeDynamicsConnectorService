# Fase 04 — Descubrimiento dinámico de entidades y columnas de Dataverse

## Implementación
- `GET /api/data/v9.2/EntityDefinitions` recupera los nombres lógicos, `EntitySetName`, clave primaria y etiquetas (según `LocalizedLabels`) para todas las tablas accesibles; para cada entidad valida campos, capacidades `IsValidForRead/Create/Update`, `IsLogical`, `SourceType`, nivel requerido, precisión y las tres familias de relaciones `OneToMany`, `ManyToOne`, `ManyToMany`.
- Choice consulta tipos derivados `PicklistAttributeMetadata`, `MultiSelectPicklistAttributeMetadata`, `StateAttributeMetadata`, `StatusAttributeMetadata`, `BooleanAttributeMetadata` para opciones numéricas y etiquetas multilingües; los derivados numéricos proporcionan `Precision` y `PrecisionSource` para importes, y Lookup/Owner/Customer proporcionan destinos.
- La API no construye consultas a tablas arbitrarias ni devuelve datos de registros: solo accede a las rutas de metadatos `EntityDefinitions` con un host HTTPS Dataverse validado, sin redirecciones ni seguimiento de `@odata.nextLink` a otros orígenes. Limita páginas, entidades, campos y tamaño de respuesta. Un error deja intacto el último snapshot válido.
- PostgreSQL: migración aditiva **004**, `dynamics.metadata_snapshots` JSONB inmutables, checksum SHA-256 y versión por conexión; compara campos/tablas nuevos, retirados y modificados. Snapshots idénticos no se duplican.
- `metadata_allowed_entities` y `metadata_allowed_fields` son allowlists explícitas **por conexión**, separadas de los grants M2M de la Fase 03. Un refresco NO habilita tablas/columnas. Los campos calculados, lógicos y de solo lectura no pueden recibir permiso de escritura aunque el administrador lo intente. Si cambia la definición de un campo se revocan las autorizaciones previas de ese campo; si se retira la tabla se deshabilita su entidad.
- Admin Vue: sección **Catálogo Dataverse** (`/metadata`), selector de conexión, refresco manual, comparación de snapshots, relaciones y Choices localizados, switches de entidad, permisos por campo y programación. Requiere sesión OIDC y antiforgery CSRF, al igual que los endpoints Admin existentes.
- Programación: `dynamics.metadata_refresh_schedules` con intervalos permitidos de 60 a 10080 minutos. Worker de servidor opcional `Metadata__SchedulerEnabled=true`, consulta pendientes cada 5 minutos y adquiere leases mediante `FOR UPDATE SKIP LOCKED`. **No** ejecuta refrescos si no se habilita en la base una conexión concreta. En Compose local el scheduler se habilita para probarlo; producción debe activarlo expresamente.

## Endpoints administrativos
Todos bajo `/api/admin/v1/connections/{code}/metadata`, con cookie segura, RBAC Admin y CSRF para las escrituras:
- `GET /`: último snapshot de la conexión, incluye entidades, campos, Choice y relaciones
- `GET /diff`: diferencias entre las dos últimas versiones
- `GET /policies`: entidades/campos habilitados
- `POST /refresh`: refresco desde Dataverse, transacción solo después de recuperar todos los metadatos
- `PUT /entities/{entity}/enabled`: habilitar/deshabilitar entidad existente en el snapshot
- `PUT /entities/{entity}/fields/{field}/policy`: política exacta `{enabled,canRead,canCreate,canUpdate}`
- `GET/PUT /schedule`: programar/refrescar intervalo por conexión

## Criterio de aceptación
Una tabla personalizada puede descubrirse desde `EntityDefinitions` sin modificar C#: aparece en la UI con sus columnas y opciones; permanece inhabilitada hasta que Admin aprueba la tabla y los campos. Se compara contra snapshots anteriores y se revocan campos modificados/retirados. No se añadió un motor de consultas genéricas: **Fase 05** permanece pendiente.

## Validación / límites
Pruebas unitarias con respuesta de metadatos simulada (nueva tabla personalizada, etiquetas Choice 3082/1033, cambio de campo calculado, diff, nombres peligrosos), más prueba PostgreSQL de snapshot inmutable, enable deny-by-default, permisos de solo lectura y revocación ante drift. Ejecutar `dotnet test DholeDynamicsConnectorService.slnx`; CI reproduce migraciones + restore cifrado F02 y aislamiento M2M F03.

**Limitación externa:** no se ejecutó contra una organización Dataverse real porque no se dispone de sus credenciales autorizadas ni de un Application User de prueba. Debe validarse la disponibilidad/capacidades de endpoints de tipos derivados de metadatos en staging antes de habilitar la función en producción. El rol Dataverse debe poder leer metadatos; nunca usar secretos históricos del legado.

Documentación de Microsoft consultada:
- https://learn.microsoft.com/en-us/power-apps/developer/data-platform/webapi/query-metadata-web-api
- https://learn.microsoft.com/en-us/power-apps/developer/data-platform/webapi/retrieve-metadata-name-metadataid
