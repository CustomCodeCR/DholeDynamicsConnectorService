import { createI18n } from 'vue-i18n'

export const messages = {
  es: {
    app: 'Dhole Dynamics Connector',
    dashboard: 'Inicio',
    architecture: 'Arquitectura',
    subtitle: 'Administración autónoma de integraciones con Dataverse',
    api: 'Estado de la API',
    live: 'Servicio',
    ready: 'Base de datos',
    healthy: 'Disponible',
    unavailable: 'No disponible',
    pending: 'Sin comprobar',
    refresh: 'Actualizar estado',
    about: 'Estado de implementación',
    phase: 'Fase 01: estructura y observabilidad',
    next: 'Las conexiones, permisos y operaciones genéricas se habilitarán cuando finalicen sus respectivas fases.',
    disclaimer: 'Esta pantalla consulta los health checks reales. No utiliza datos simulados.',
    independent: 'Contrato HTTPS independiente, sin DholeApiGateway',
    error: 'No se pudo consultar la API. Revise la conexión o el despliegue.',
  },
  en: {
    app: 'Dhole Dynamics Connector',
    dashboard: 'Dashboard',
    architecture: 'Architecture',
    subtitle: 'Standalone Dataverse integration administration',
    api: 'API health',
    live: 'Service',
    ready: 'Database',
    healthy: 'Available',
    unavailable: 'Unavailable',
    pending: 'Not checked',
    refresh: 'Refresh status',
    about: 'Implementation status',
    phase: 'Phase 01: foundation and observability',
    next: 'Connections, permissions and generic operations will be enabled only after their corresponding phases are complete.',
    disclaimer: 'This page uses real health endpoints, never mocked production data.',
    independent: 'Standalone HTTPS contract with no DholeApiGateway dependency',
    error: 'Could not reach API. Check the connection or deployment.',
  },
}

export const i18n = createI18n({
  legacy: false,
  locale: 'es',
  fallbackLocale: 'en',
  messages,
})
