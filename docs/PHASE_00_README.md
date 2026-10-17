# Fase 00 — auditoría y arquitectura

Esta carpeta contiene la línea base **propuesta** para evolucionar `DholeDynamicsConnectorService`. El diseño requiere revisión humana y el legado contiene credenciales históricas que deben rotarse. No se implementaron motor CRUD, UI, autenticación autónoma ni despliegues en esta fase.

- [Auditoría del prototipo y legado](architecture/PHASE_00_AUDIT.md)
- [Arquitectura](architecture/COMPONENTS.md) · [Secuencias CRUD](architecture/CRUD_SEQUENCE.md) · [Seguridad](architecture/SECURITY_MODEL.md)
- [Fronteras con consumidores](architecture/INTEGRATION_BOUNDARIES.md)
- [Threat model](security/THREAT_MODEL.md) · [Rotación legacy](security/LEGACY_SECRET_ROTATION.md)
- [Inventario de 88 operaciones](migration/LEGACY_INVENTORY.md) · [CSV trazable](migration/LEGACY_COVERAGE.csv)
- [ADRs](decisions/) · [Backlog](backlog/PHASE_00_BACKLOG.md) · [Riesgos y gates](backlog/PHASE_00_RISKS.md)

Validar artefactos (sin red): `python3 scripts/verify-phase00-docs.py`. La ejecución de build/test .NET y pruebas contra Dataverse **no se afirma** en Fase 00.
