<script setup lang="ts">
import { onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useHealthStore, type HealthState } from '../stores/health'

const { t } = useI18n()
const health = useHealthStore()
const label = (state: HealthState) => t(state === 'healthy' ? 'healthy' : state === 'unknown' ? 'pending' : 'unavailable')
onMounted(() => { void health.refresh() })
</script>

<template>
  <section aria-labelledby="health-heading">
    <div class="mb-6 flex flex-wrap items-center justify-between gap-4">
      <h2 id="health-heading" class="text-2xl font-semibold">{{ t('api') }}</h2>
      <button type="button" :disabled="health.busy" class="rounded-lg bg-slate-900 px-4 py-2 font-medium text-white hover:bg-slate-700 disabled:opacity-50" @click="health.refresh">
        {{ t('refresh') }}
      </button>
    </div>
    <div class="grid gap-5 sm:grid-cols-2" aria-live="polite">
      <article class="rounded-xl border border-slate-200 bg-white p-6 shadow-sm">
        <h3 class="text-sm font-semibold text-slate-600">{{ t('live') }}</h3>
        <p class="mt-3 text-2xl font-semibold" :class="health.live === 'healthy' ? 'text-emerald-700' : 'text-slate-800'">{{ label(health.live) }}</p>
      </article>
      <article class="rounded-xl border border-slate-200 bg-white p-6 shadow-sm">
        <h3 class="text-sm font-semibold text-slate-600">{{ t('ready') }}</h3>
        <p class="mt-3 text-2xl font-semibold" :class="health.ready === 'healthy' ? 'text-emerald-700' : 'text-slate-800'">{{ label(health.ready) }}</p>
      </article>
    </div>
    <p v-if="health.failed" role="alert" class="mt-4 text-sm text-red-700">{{ t('error') }}</p>
    <p class="mt-5 text-sm text-slate-600">{{ t('disclaimer') }}</p>
  </section>
</template>
