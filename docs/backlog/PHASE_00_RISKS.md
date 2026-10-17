# Registro de riesgos y puertas de control — Fase 00

| Riesgo | Probabilidad | Impacto | Tratamiento | Owner requerido | Estado |
|---|---|---|---|---|---|
| Exposición de credenciales legado | Alta (exposición), uso desconocido | Crítico | Revocación inmediata / revisión de logs | Entra/security | Abierto |
| Token M2M permite acceso entre conexiones | Alta en prototipo actual | Crítico | Scope por conexión, entity, campo y cliente | Backend/security | Abierto |
| Interpolación OData sin restricciones | Confirmada en legado | Alto | DSL tipado y escaped parameters | Backend | Abierto |
| Contratos específicos Dhole en prototipo | Confirmado | Alto | Contratos independientes y mapping genérico | Arquitectura | Abierto |
| Esquema/DB no coincide con objetivo | Confirmado | Alto | Plan de migración no destructivo | DBA | Abierto |
| Sin test/CI automatizado en main | Confirmado | Alto | F01 pipeline con pruebas | DevEx | Abierto |
| Falta de metadatos Dataverse reales | Confirmado pendiente | Alto | Sandbox + discovery y revisión | Admin Dynamics | Abierto |
| Borrado irreversible por CRUD | No implementado | Crítico | Permiso reforzado y confirmación | Owner negocio | Abierto |
| Ambigüedad de POST tras caída | Parcialmente mitigada | Alto | Reservas, UNKNOWN + reconciliación | Backend | Abierto |

## Puerta de aceptación F00
- [x] Lectura de la línea base de `main`; creación controlada de `develop` y `master`.
- [x] Auditoría documental del prototipo y del legado.
- [x] 88/88 endpoints clasificados y referenciados.
- [x] Diagramas de componentes, CRUD y modelo de seguridad.
- [x] ADRs, backlog y matriz de riesgos publicados.
- [ ] Revisión/aprobación humana de arquitectura, ADRs y criterios de inventario.
- [ ] Validación de estado/revocación de los secretos históricamente expuestos.
- [ ] Pruebas de seguridad y cobertura de integración correspondientes a fases posteriores.

**Estado operativo:** F00 documentada pero **NO APROBADA/CERRADA**. Solo se puede iniciar F01 después de cumplir y registrar gates de seguridad y aprobación.
