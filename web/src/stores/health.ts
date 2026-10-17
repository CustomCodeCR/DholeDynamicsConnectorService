import { defineStore } from 'pinia'
import { ref } from 'vue'

export type HealthState = 'unknown' | 'healthy' | 'unavailable'

export const useHealthStore = defineStore('health', () => {
  const live = ref<HealthState>('unknown')
  const ready = ref<HealthState>('unknown')
  const busy = ref(false)
  const failed = ref(false)

  async function refresh(): Promise<void> {
    busy.value = true
    failed.value = false
    try {
      const [liveness, readiness] = await Promise.all([
        fetch('/health/live', { cache: 'no-store' }),
        fetch('/health/ready', { cache: 'no-store' }),
      ])
      live.value = liveness.ok ? 'healthy' : 'unavailable'
      ready.value = readiness.ok ? 'healthy' : 'unavailable'
    } catch {
      live.value = 'unavailable'
      ready.value = 'unavailable'
      failed.value = true
    } finally {
      busy.value = false
    }
  }
  return { live, ready, busy, failed, refresh }
})
