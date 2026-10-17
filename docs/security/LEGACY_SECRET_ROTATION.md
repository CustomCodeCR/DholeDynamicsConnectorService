# Revocación y rotación de credenciales históricas — Fase 00

**Hallazgo:** `MauLang18/DynaConnect/DynamicsCF/DynamicsCF/Controllers/ValuesController.cs` contiene asignaciones de identificadores y credenciales de aplicación incrustadas en endpoints; el repositorio legado es público y conserva perfiles/artefactos. No se copian aquí valores sensibles ni se asume que sigan vigentes.

## Procedimiento seguro (requiere responsables de Entra y Dynamics)
1. Registrar incidente/owner y verificar alcance, identidades de aplicaciones, recursos, permisos y periodo expuesto sin difundir secretos.
2. Comprobar si las credenciales siguen activas **en la consola segura de Microsoft**, nunca probándolas desde este proyecto.
3. Revocar o expirar inmediatamente cualquier credential comprometida; emitir nuevo secreto/certificado con validez y roles mínimos. Si hay duda de abuso, activar respuesta a incidentes.
4. Revisar sign-in logs de service principals, aplicaciones empresariales, Dataverse y actividad anómala desde el momento de posible exposición; preservar evidencia de auditoría.
5. Actualizar exclusivamente el gestor seguro de secretos del servicio nuevo/legado que aún opere; no insertar credenciales en Git, GitHub Actions YAML, PRs, appsettings, captures o logs.
6. Validar nueva identidad en sandbox, restringir Application User, establecer fecha de expiración y alerta preventiva.
7. Ejecutar escaneo histórico del repositorio, excluir/retirar `bin/`, `obj/`, `.vs/`, `*.pubxml.user` y perfiles potencialmente sensibles mediante PR separado y revisión. Un rewrite de historia exige coordinación; **rotar primero**.
8. Registrar ticket, fecha de rotación, responsables y evidencia saneada de cierre; no archivar valores secretos.

**Estado:** procedimiento documentado; **no se ha revocado, rotado ni probado ninguna credencial**. Tratamiento prioritario antes de ensayos reales. No modificar el legado en esta fase.
