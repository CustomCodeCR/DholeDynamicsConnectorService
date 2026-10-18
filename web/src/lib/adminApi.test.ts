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
})
