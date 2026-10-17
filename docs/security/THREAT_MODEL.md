# Threat model STRIDE — Fase 00

**Contexto:** servicio público autónomo que maneja credenciales para Dataverse, datos comerciales y capacidades de escritura. Estado: análisis documental, no pentest ni verificación de controles en runtime.

| ID | Amenaza / activo | Escenario | Impacto | Control requerido | Fase |
|---|---|---|---|---|---|
| T01 | Spoofing M2M | Token con issuer, audience, firma o client identity falsificados | Crítico | OAuth2 JWT de autoridad verificable + scopes y cliente habilitado | 03 |
| T02 | Elevación de privilegios | Scope genérico accede a otra conexión/entidad/columna | Crítico | Deny-by-default RBAC/ABAC, pruebas entre organizaciones | 03–06 |
| T03 | Filtración de secretos | Clave o client secret en Git, trazas, UI o backups | Crítico | Revocación legado, vault/AES-GCM, redacción, backup cifrado | 00/02 |
| T04 | Inyección de filtros | Interpolación de nombres de parámetros en URL OData | Alto | DSL tipado, whitelist metadata, escaping y límites de costo | 05 |
| T05 | SSRF | URL de organización/webhook apuntando a host ajeno o redirect | Alto | HTTPS estricto, allowlist, DNS y redirects bloqueados, egress | 03/11 |
| T06 | Tampering | Modificación de campos calculados o políticas no versionadas | Alto | Metadata capabilities, ETags, historial, rollback y firma lógica | 04/07 |
| T07 | Repudio | Cambios sin actor ni correlación o auditoría adulterada | Alto | Registro inmutable lógico, correlation ID, control de acceso y retención | 02/06 |
| T08 | Doble creación | POST se ejecuta y respuesta se pierde | Alto | Idempotency Key + estado UNKNOWN + reconciliación; no blind retry | 06/11 |
| T09 | Denegación de servicio | Consulta OData cara, lotes enormes, 429 encadenado | Alto | Rate limits, paginación máxima, cuotas, Retry-After/backoff | 05/11 |
| T10 | Contaminación entre entornos | Staging usa llave/organización de producción | Crítico | DB, keys, application users y secretos separados | 02/12 |
| T11 | Pérdida de auditoría | Job falla o se reintenta sin checkpoint | Alto | Cola durable, dead letter, checkpoints, health & metrics | 11 |
| T12 | Supply-chain/legado | Binarios, .vs, artefactos y perfiles históricos en repo | Alto | Inventario/escaneo secreto, dependencia, SBOM y limpieza revisada | 00/01 |
| T13 | Datos sensibles/PII | Export excesivo de datos de clientes | Alto | Minimización, permisos por campo, retención, cifrado, auditoría | 03/05 |
| T14 | Borrado irreversible | DELETE masivo/sin permisos o por error | Crítico | Scope reforzado, doble confirmación, deny en prod hasta aprobación | 06/12 |

## Trust boundaries
1. Navegador Admin → API: tratar todo input como no confiable; protección CSRF si cookies, CORS restrictivo y CSP.
2. Consumidor HTTP → runtime: no compartir clave ni cookies de Admin.
3. Runtime → PostgreSQL: credencial DB mínima, parametrización y segregación; clave maestra nunca reside allí.
4. Runtime → Dataverse: OAuth client credentials, Application User mínimo y errores sanitizados.
5. Operador → despliegue: cambios auditados, approvals, backups, sin cambios en producción desde esta fase.

## Hallazgos del código actual
- Se validan scopes por claim en el prototipo, pero no hay autorización granular por conexión/entidad/atributo; caso T02 pendiente.
- El legado construye GET OData con interpolación de `name`/`name2` y contiene secreto de aplicación literal; casos T03/T04 críticos.
- `QuoteRepository` ya tiene reservas y UNKNOWN, una base útil que aún requiere reconciliación y estados durables.
- No se ejecutaron pentests, pruebas de integración o consultas contra tenants.
