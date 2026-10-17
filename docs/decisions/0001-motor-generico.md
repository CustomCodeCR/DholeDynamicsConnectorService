# ADR 0001 — Motor guiado por metadatos

- **Estado:** Propuesto (Fase 00; pendiente revisión/aprobación)
- **Fecha:** 2026-10-17
- **Contexto:** `DholeDynamicsConnectorService` debe evolucionar desde prototipo Quote a plataforma Dataverse autónoma y gobernada.

## Decisión propuesta
Diseñar un motor único Dataverse con tabla y campo habilitado por políticas. Pros: extensibilidad sin endpoint por entidad. Contras: complejidad de filtros, metadatos y validación. Evitar código dinámico de usuario, URL/OData arbitrarios y lógica específica de tabla salvo extensión explícita. Supuestos y pruebas en F04–07.

## Consecuencias y reversibilidad
Documentar pruebas de contrato/seguridad antes de implementar; toda excepción requiere un ADR nuevo, pruebas y aprobación. Este ADR **no** es código operativo ni permiso para desplegar.
