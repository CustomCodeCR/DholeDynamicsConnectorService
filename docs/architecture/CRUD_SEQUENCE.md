# Secuencias CRUD y comportamiento de escritura — Fase 00

```mermaid
sequenceDiagram
 participant C as Cliente M2M / Admin
 participant API as API propia
 participant P as AuthZ + Policies
 participant DB as PostgreSQL
 participant D as Dataverse
 C->>API: Solicitud autenticada /api/v1
 API->>P: Validar token, conexión, tabla, operación, columnas
 P->>DB: Consultar permisos y metadatos habilitados
 alt Denegado
  P-->>API: 403/404 seguro
  API-->>C: ProblemDetails + correlationId
 else Permitido
  P-->>API: Contexto autorizado
  API->>DB: Registrar operación / idempotency key
  API->>D: GET/POST/PATCH/DELETE con headers OData
  alt Respuesta verificable
   D-->>API: GUID, ETag, nextLink o resultado
   API->>DB: Confirmar estado y auditar
   API-->>C: Contrato estable / resultado
  else Red o escritura ambigua
   API->>DB: Marcar UNKNOWN; no repetir create
   API-->>C: 202/409/502 con operationId
  end
 end
```

- **GET one:** GUID autorizado, columnas allowlisted; 200/404 y ETag.
- **Search many:** filtro DSL validado; top/paginación máxima; `@odata.nextLink` como token opaco, nunca URL arbitraria del cliente.
- **Create:** validar Choice/lookups/obligatoriedad; reserva idempotente antes de POST; 201 solo tras GUID verificable.
- **PATCH:** payload parcial allowlisted; `If-Match` y 412 por conflicto; nunca sobrescribir cambios concurrentes silenciosamente.
- **DELETE:** scope reforzado y confirmación, registro de auditoría y retención; sin asumir recuperación.
- **Upsert:** solo Alternate Keys observadas/habilitadas; prevenir duplicados.
- **Batch:** límites, respuestas individuales, atomicidad solo si lo garantiza Dataverse, no transacción distribuida.
- **Retry:** 429/Retry-After y 5xx elegibles con backoff/jitter; escrituras inciertas van a reconciliación.

Este diseño no autoriza todavía activar escrituras ni eliminación en producción.
