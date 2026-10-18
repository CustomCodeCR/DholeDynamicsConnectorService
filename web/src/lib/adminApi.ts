import { ref } from 'vue'

export type Session = { authenticated: boolean; actor: string; securityAdmin: boolean; csrfToken: string }
export type Connection = { id: string; code: string; dataverseUrl: string; tenantId: string; clientId: string }
export type Choice = { value: number; labels: Record<string, string> }
export type Field = { logicalName: string; type: string; canRead: boolean; canCreate: boolean;
  canUpdate: boolean; isCalculated: boolean; requiredLevel?: string | null;
  precision?: number | null; choices: Choice[]; lookupTargets: string[] }
export type Relation = { referencingNavigation?: string | null; referencedEntity?: string | null; referencingAttribute?: string | null }
export type Entity = { logicalName: string; entitySetName: string; primaryIdAttribute: string;
  columns: Field[]; relationships: Relation[] }
export type Policies = {
  entities: Array<{ logicalName: string; enabled: boolean }>
  fields: Array<{ entityName: string; fieldName: string; enabled: boolean;
    canRead: boolean; canCreate: boolean; canUpdate: boolean }>
}
export type RecordPage = { items: Array<Record<string, unknown>>; nextCursor: string | null;
  count?: number | null; page: number; metadataVersion: number; truncated: boolean }
export type RecordDetail = { item: Record<string, unknown>; eTag: string | null; metadataVersion: number }
export type WriteResult = { operationId: string; state: string; recordId: string | null; httpStatus: number | null }

export function useAdminApi() {
  const session = ref<Session | null>(null)
  const busy = ref(false)
  const error = ref('')
  const message = ref('')

  async function request<T>(path: string, method = 'GET', body?: unknown, extra?: Record<string,string>): Promise<T> {
    if (!path.startsWith('/api/admin/v1/') && path !== '/auth/session')
      throw new Error('Unsupported admin URL')
    const headers: Record<string, string> = { ...extra }
    if (body !== undefined) headers['Content-Type'] = 'application/json'
    if (method !== 'GET') {
      if (!session.value?.csrfToken) throw new Error('Admin session required')
      headers['X-CSRF-TOKEN'] = session.value.csrfToken
    }
    const response = await fetch(path, {
      method, credentials: 'same-origin', cache: 'no-store', headers,
      ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null) as
        { title?: string; code?: string; operationId?: string; state?: string } | null
      // Ambiguous/rejected writes still carry a durable operation ID. Preserve it
      // so the editor can prevent a second dispatch.
      if (path.startsWith('/api/admin/v1/console/records/') &&
          (method==='POST' || method==='PATCH' || method==='DELETE') &&
          problem?.operationId && (problem.state==='ambiguous' || problem.state==='rejected'))
        return problem as T
      throw new Error((problem?.title ?? 'Request rejected') + ' (HTTP ' + response.status + ')')
    }
    return response.status === 204 ? null as T : await response.json() as T
  }
  async function loadSession(): Promise<boolean> {
    const response = await fetch('/auth/session', { credentials: 'same-origin', cache: 'no-store' })
    if (!response.ok) { session.value = null; return false }
    session.value = await response.json() as Session
    return true
  }
  async function run<T>(fn: () => Promise<T>): Promise<T | undefined> {
    busy.value = true
    error.value = ''
    message.value = ''
    try { return await fn() }
    catch (reason) { error.value = reason instanceof Error ? reason.message : 'Request rejected'; return undefined }
    finally { busy.value = false }
  }
  async function loadCatalog(code: string) {
    const prefix = '/api/admin/v1/connections/' + encodeURIComponent(code) + '/metadata'
    const [snapshot, policies] = await Promise.all([
      request<{catalog:{tables:Entity[]};version:number}>(prefix),
      request<Policies>(prefix+'/policies'),
    ])
    const entities = snapshot.catalog.tables.filter(e =>
      policies.entities.some(p => p.logicalName === e.logicalName && p.enabled))
    return { entities, policies, version: snapshot.version }
  }
  function allowedFields(entity: Entity, policies: Policies, operation: 'read'|'create'|'update'): Field[] {
    return entity.columns.filter(field => {
      const rule = policies.fields.find(p => p.entityName === entity.logicalName &&
        p.fieldName === field.logicalName && p.enabled)
      return rule && field[operation === 'read' ? 'canRead' : operation === 'create' ? 'canCreate' : 'canUpdate'] &&
        (operation === 'read' ? rule.canRead : operation === 'create' ? rule.canCreate : rule.canUpdate) &&
        (operation === 'read' || !field.isCalculated)
    })
  }
  return { session, busy, error, message, request, run, loadSession, loadCatalog, allowedFields }
}

// A downloaded CSV must not become an executable spreadsheet formula.
export function safeCsv(rows: Array<Record<string, unknown>>, fields: string[]): string {
  const quote = (value: unknown): string => {
    const raw = value == null ? '' : typeof value === 'object' ? JSON.stringify(value) : String(value)
    const safe = /^[=+\-@\t\r]/.test(raw) ? "'" + raw : raw
    return '"' + safe.replaceAll('"', '""') + '"'
  }
  return [fields.map(quote).join(','), ...rows.map(row =>
    fields.map(field => quote(row[field])).join(','))].join('\r\n')
}
export function downloadCsv(contents: string, filename: string) {
  const blob = new Blob(['\ufeff', contents], { type:'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url; link.download = filename
  document.body.append(link); link.click(); link.remove()
  URL.revokeObjectURL(url)
}
