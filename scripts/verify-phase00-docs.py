#!/usr/bin/env python3
"""Valida cobertura y estructura de artefactos de Fase 00 sin acceder a entornos externos."""
from collections import Counter
from pathlib import Path
import csv

root = Path(__file__).resolve().parents[1]
path = root / "docs" / "migration" / "LEGACY_COVERAGE.csv"
with path.open(encoding="utf-8", newline="") as handle:
    rows = list(csv.DictReader(handle))
assert len(rows) == 88, f"Esperados 88 endpoints, hay {len(rows)}"
assert Counter(row["metodo"] for row in rows) == {"GET": 87, "PATCH": 1}
assert {row["id"] for row in rows} == {f"LEG-{i:03d}" for i in range(1, 89)}
assert len({(row["metodo"], row["ruta_legacy"]) for row in rows}) == len(rows)
for row in rows:
    for key in ("id", "ruta_legacy", "familia", "entidad_legacy_sin_validar", "clasificacion_destino", "fase_objetivo", "estado", "referencia_archivo", "linea_inicio"):
        assert row[key], f"{row['id']}: falta {key}"
    assert row["fase_objetivo"] == "09"
    assert row["estado"].startswith("Planificada")
required = [
    "docs/architecture/PHASE_00_AUDIT.md",
    "docs/architecture/COMPONENTS.md",
    "docs/architecture/CRUD_SEQUENCE.md",
    "docs/architecture/SECURITY_MODEL.md",
    "docs/architecture/INTEGRATION_BOUNDARIES.md",
    "docs/security/THREAT_MODEL.md",
    "docs/security/LEGACY_SECRET_ROTATION.md",
    "docs/migration/LEGACY_INVENTORY.md",
    "docs/backlog/PHASE_00_BACKLOG.md",
    "docs/backlog/PHASE_00_RISKS.md",
]
required += [f"docs/decisions/{filename}" for filename in (
    "0001-motor-generico.md", "0002-postgresql-dedicado.md",
    "0003-contratos-autenticacion.md", "0004-errores-idempotencia.md",
    "0005-secretos-y-rotacion.md", "0006-politicas-acceso.md",
    "0007-jobs-resiliencia.md"
)]
assert all((root / name).is_file() for name in required)
print(f"PASS Fase 00: {len(rows)} endpoints (87 GET, 1 PATCH), rutas únicas y {len(required)} documentos esenciales.")
