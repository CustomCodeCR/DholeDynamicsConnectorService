import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useHealthStore } from './health'

describe('health store', () => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => vi.unstubAllGlobals())
  it('reads actual endpoint responses', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce({ ok: true })
      .mockResolvedValueOnce({ ok: false })
    vi.stubGlobal('fetch', fetchMock)
    const store = useHealthStore()
    await store.refresh()
    expect(store.live).toBe('healthy')
    expect(store.ready).toBe('unavailable')
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })
  it('fails closed when the backend is unreachable', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('offline')))
    const store = useHealthStore()
    await store.refresh()
    expect(store.failed).toBe(true)
    expect(store.live).toBe('unavailable')
  })
})
