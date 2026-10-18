# Fase 12 — Matriz E2E staging y checklist vinculante de go-live

**Estado inicial: NOT_RUN.** No hay autoridad Microsoft Entra, cliente M2M, cuenta Dataverse ni hostname TLS real suministrados. Un test simulado no cambia ese estado.

En cada ejecución completar: fecha UTC, ambiente/host, SHA, conexión, ejecutor, ticket, resultado PASS/FAIL/NOT_RUN, IDs de prueba descartables y enlaces de evidencia. No copiar tokens, contraseñas, payloads personales, nombres de clientes ni encabezados Authorization.

| ID | Caso de aceptación | Procedimiento staging | Condición de PASS |
|---|---|---|---|
| E01 | TLS, DNS y aislamiento | Verificar Caddy ACME, 443, HSTS, puerto DB no publicado, `release-smoke.sh` | Certificado válido, sólo 80/443 visibles, 401 anónimos |
| E02 | Login Admin OIDC | Navegador: entrar a `/auth/login` con identidad humana autorizada y comprobar `/auth/session`, logout y sesión expirada | Redirección y PKCE válidos, CSRF requerido, roles correctos |
| E03 | Login denegado/roles | Probar sin roles, M2M JWT en Admin y usuario sin SecurityAdmin | 401/403, sin escalada ni políticas editables |
| E04 | Conexión Dataverse | UI Administración: conexión staging, test de credenciales e identidad del tenant | Test a organización staging, nunca prod |
| E05 | Metadata y políticas | Actualizar metadata, inspeccionar Choice/lookup, activar solo tabla y columnas permitidas | Descubrimiento y snapshots reales, sin acceso a campos negados |
| E06 | Query/search | Ejecutar búsqueda tipada, filtrado y cursor paginado con un app-only JWT registrado | M2M grants mínimos, cuota y paginación, sin URL libre |
| E07 | CRUD + ETag | Crear fila descartable, leer, actualizar con ETag correcto, repetir con ETag viejo; borrar sólo si autorizado **staging** | 201/200, 412 en ETag stale, evidencia de auditoría, sin duplicado |
| E08 | Plantilla Quote | Previsualizar/CreateQuote activado y aprobado, enviar `DholeRateId` descartable e Idempotency-Key determinista; repetir clave | Una sola oferta Dynamics, vínculo persistente; resultado incierto requiere conciliación |
| E09 | Delta/sync | Habilitar entidad de prueba, procesar varias páginas, detener worker y reanudar desde checkpoint; revisar tombstones | Continuidad y dedup, sin duplicación de cambios |
| E10 | Quota 429 | Emular/responder 429 con `Retry-After` en host de prueba y probar reintento GET limitado | Backoff y circuit breaker correctos, sin reintentos de escrituras |
| E11 | Error inesperado | Causar 5xx o desconexión controlada, inspeccionar ProblemDetails y logs | Correlación y redacción; no exponer secreto/PII |
| E12 | Backup y restore | `pg_dump | age` a archivo nuevo, restaurar en sandbox vacío con clave externa de staging | Datos y migraciones íntegros, secretos descifrables; restaurar sobre origen es rechazado |
| E13 | Deploy fallido / rollback | Desplegar candidato defectuoso controlado, verificar aborto y vuelta a SHA anterior | Health vuelve, sin `down -v`, sin eliminación de datos |
| E14 | Evolución esquema | Reaplicar migración, fallar checksum modificado, verificar imagen anterior con esquema nuevo | Replay idempotente y rollback solo compatible |
| E15 | Revocación / retención | Deshabilitar cliente y grants, revisar export auditoría, ejecutar política de retención | Revocado recibe 403 y no se eliminan dead-letters ni ledger |
| E16 | Independencia | Apagar servicios DholeSystem/Pricing del sandbox sin modificar la DB del conector | Admin/API/worker independientes y saludables |

**Regla**: todos los casos E01–E16 deben resultar PASS con evidencia verificable antes de firmar go-live. Si un caso no aplica, documentar quién aprobó la excepción; nunca convertir NOT_RUN automáticamente en PASS.

## Secuencia de aprobaciones

- [ ] Auditoría externa y rotación de secretos legacy verificadas (Entra/operaciones)
- [ ] Todas las verificaciones automáticas verdes sobre el SHA exacto
- [ ] E01–E16 staging con evidencia y responsables
- [ ] Backup age y recuperación en staging con clave real
- [ ] SLOs y alertas aceptadas por operaciones y soporte
- [ ] DNS, TLS, firewall e identidad de producción verificados
- [ ] Aprobación de Environment GitHub Production y ticket de cambio
- [ ] Producción desplegada inicialmente **sin permisos de escritura**, con smoke HTTPS
- [ ] Aprobación adicional para habilitar CREATE/UPDATE/UPSERT
- [ ] Aprobación adicional e independiente para DELETE/acciones destructivas

**Decisión por defecto: NO-GO para producción.** Solo un operador humano con acceso real al tenant, host, Vault y tickets puede levantar los bloqueos. Ningún commit activa permisos de escritura ni despliega producción por sí solo.
