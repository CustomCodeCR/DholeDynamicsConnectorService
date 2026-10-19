# Registrar Tenant ID y Client ID de Dataverse en PostgreSQL Dhole

Los ID **no van en `.env`**. Se guardan en **`dhole_dynamics_connector`**, tabla **`dynamics.connections`**; el secreto se cifra en la API con la clave persistente exclusiva del conector.

1. Ejecutar primero deploy del stack nuevo en **staging** desde el workflow manual `deploy-dhole-gateway.yml`. Este crea la base si no existe y aplica las migraciones, sin alterar otras bases.
2. En DholeAuth asignar el scope **`dynamics.admin`** a tu usuario (no está asignado automáticamente). Volver a iniciar sesión para recibir un JWT nuevo que incluya el scope.
3. Desde un terminal de confianza, ejecutar `python3 scripts/register-dataverse-connection.py staging` o `production`. El script solicita:
    - **Code**: identificador interno, por ejemplo `principal`
    - **DataverseUrl**: `https://<organizacion>.crm.dynamics.com`
    - **Tenant ID**: GUID del directorio de Microsoft de la organización Dataverse
    - **Client ID**: GUID de la aplicación que tiene permisos como Application User en Dataverse
    - **Client Secret VALUE**: valor secreto de esa aplicación, **no** el Secret ID
    - **DefaultCurrencyId**: GUID de moneda opcional, no inventarlo
    - JWT válido del usuario DholeAuth
4. El script envía `POST https://{ambiente}/api/dynamics-admin/connections` por HTTPS usando el token y los valores reales. La API inserta/actualiza la fila en `dynamics.connections` y cifra el secreto AES-256-GCM. No se imprimen ni guardan los secretos.
5. Para listar conexiones: `GET /api/dynamics-admin/connections` con el JWT. Para verificar acceso real a Dataverse: `POST /api/dynamics-admin/connections/{code}/test` con el mismo token. Un 200 en salud del gateway prueba solo DB, **no la conexión Microsoft**.

Importante: No insertés el secret con `INSERT` manual: la API necesita guardar su nonce, tag y versión de cifrado. No copies los ID de Microsoft a las variables de DholeAuth/JWT. Staging y producción tienen bases distintas aunque usen el mismo nombre dentro de contenedores/redes diferentes.

Revisar [topología y despliegue](https://github.com/CustomCodeCR/DholeSystem/blob/master/docs/deployment/dynamics-gateway.md).
