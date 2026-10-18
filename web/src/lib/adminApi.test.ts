import { describe, expect, it } from 'vitest'
import { safeCsv } from './adminApi'
describe('admin console CSV export', () => {
  it('neutralizes formula injection in every selected field', () => {
    const output = safeCsv([{name:'=SUM(1,1)', detail:'+cmd', value:'-4', auth:'@user'}],
      ['name','detail','value','auth'])
    expect(output).toContain('"\'=SUM(1,1)"')
    expect(output).toContain('"\' +cmd"'.replace(' ', ''))
    expect(output).toContain('"\'-4"')
    expect(output).toContain('"\'@user"')
  })
  it('escapes delimiters, quotes and only includes approved columns', () => {
    expect(safeCsv([{visible:'a,"b"',secret:'never'}],['visible']))
      .toBe('"visible"\r\n"a,""b"""')
  })
  it('preserves an ambiguous write id to prevent accidental duplicate submission', async () => {
    const { useAdminApi } = await import('./adminApi')
    const { vi } = await import('vitest')
    const admin = useAdminApi()
    admin.session.value = { authenticated: true, actor:'admin', securityAdmin:true, csrfToken:'csrf' }
    vi.stubGlobal('fetch',vi.fn().mockResolvedValue({
      ok:false,status:409,json:async()=>({operationId:'operation-guid',state:'ambiguous'})
    }))
    const outcome = await admin.request<{operationId:string;state:string}>(
      '/api/admin/v1/console/records/new_shipment','POST',{connectionCode:'demo',values:{new_name:'test'}})
    expect(outcome.state).toBe('ambiguous')
    expect(outcome.operationId).toBe('operation-guid')
    vi.unstubAllGlobals()
  })

})
