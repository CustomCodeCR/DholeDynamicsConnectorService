<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAdminApi, type Connection } from '../lib/adminApi'

type LegacyItem = {
  id: string; route: string; family: string; entity: string; status: string
  queryName: string; templateName: string; reasons: string[]
  requiredReadFields: string[]; metadataVersion: number | null
}
type LegacyReport = {
  format: string; sourceCommit: string; total: number; ready: number; staged: number
  blocked: number; items: LegacyItem[]
}
const { t } = useI18n()
const api = useAdminApi()
const auth = ref(false)
const connections = ref<Connection[]>([])
const code = ref('')
const preview = ref<LegacyReport | null>(null)
const history = ref<LegacyItem[]>([])
const confirmation = ref(false)
const selected = ref('all')
const filtered = computed(() => preview.value?.items.filter(
  item => selected.value === 'all' || item.status === selected.value) ?? [])
const base = () => '/api/admin/v1/connections/' + encodeURIComponent(code.value) + '/legacy-migration'

async function inspect() {
  await api.run(async () => {
    if (!code.value) return
    preview.value = await api.request<LegacyReport>(base() + '/preview')
    history.value = await api.request<LegacyItem[]>(base() + '/history')
    confirmation.value = false
  })
}
async function stage() {
  await api.run(async () => {
    if (!api.session.value?.securityAdmin || !confirmation.value || !code.value)
      throw new Error(t('legacyConfirmFirst'))
    const result = await api.request<LegacyReport>(base() + '/import', 'POST', { confirm: true })
    preview.value = result
    history.value = await api.request<LegacyItem[]>(base() + '/history')
    confirmation.value = false
    api.message.value = t('legacyImportFinished')
  })
}
onMounted(async () => {
  auth.value = await api.loadSession()
  if (auth.value) connections.value = await api.request<Connection[]>('/api/admin/v1/connections')
})
</script>

<template>
  <section class="space-y-5">
    <h2 class="text-2xl font-semibold">{{ t('legacyTitle') }}</h2>
    <p class="text-sm text-slate-600">{{ t('legacyIntro') }}</p>
    <p v-if="!auth" role="alert">{{ t('signInRequired') }}</p>
    <template v-else>
      <label class="block max-w-lg text-sm">{{ t('selectConnection') }}
        <select v-model="code" class="mt-2 block w-full rounded border p-2" @change="inspect">
          <option value="">{{ t('selectConnection') }}</option>
          <option v-for="conn in connections" :key="conn.id" :value="conn.code">{{ conn.code }}</option>
        </select>
      </label>
      <p v-if="api.error.value" class="text-sm text-red-700" role="alert">{{ api.error.value }}</p>
      <p v-if="api.message.value" class="text-sm text-green-800" role="status">{{ api.message.value }}</p>
      <template v-if="preview">
        <div class="grid gap-3 sm:grid-cols-4">
          <div v-for="stat in [
            { key: 'legacyTotal', value: preview.total },
            { key: 'legacyReady', value: preview.ready },
            { key: 'legacyStaged', value: preview.staged },
            { key: 'legacyBlocked', value: preview.blocked }
          ]" :key="stat.key" class="rounded border bg-white p-4">
            <p class="text-sm text-slate-600">{{ t(stat.key) }}</p>
            <p class="text-2xl font-bold">{{ stat.value }}</p>
          </div>
        </div>
        <section class="space-y-3 rounded border bg-white p-4">
          <h3 class="font-semibold">{{ t('legacyImportTitle') }}</h3>
          <p class="text-sm text-slate-600">{{ t('legacyImportExplanation') }}</p>
          <label v-if="api.session.value?.securityAdmin" class="flex items-start gap-2 text-sm">
            <input v-model="confirmation" type="checkbox" class="mt-1">
            <span>{{ t('legacyConfirmFirst') }}</span>
          </label>
          <button v-if="api.session.value?.securityAdmin" type="button" :disabled="api.busy.value || !confirmation"
            class="rounded bg-slate-900 px-4 py-2 text-sm text-white disabled:opacity-50" @click="stage">
            {{ t('legacyStage') }}
          </button>
          <p v-else class="text-sm">{{ t('uiSecurityAdminOnly') }}</p>
        </section>
        <div class="flex flex-wrap items-center gap-3">
          <label class="text-sm">{{ t('legacyFilter') }}
            <select v-model="selected" class="ml-2 rounded border p-2">
              <option value="all">{{ t('legacyAll') }}</option>
              <option v-for="state in ['ready_to_stage','already_staged','staged','blocked_source','needs_configuration','name_conflict']"
                :key="state" :value="state">{{ state }}</option>
            </select>
          </label>
          <button type="button" :disabled="api.busy.value" class="rounded border px-3 py-2 text-sm"
            @click="inspect">{{ t('refresh') }}</button>
        </div>
        <div class="overflow-x-auto rounded border bg-white">
          <table class="min-w-[740px] w-full text-left text-sm">
            <thead><tr class="bg-slate-100">
              <th class="p-3">ID</th><th class="p-3">{{ t('legacyRoute') }}</th>
              <th class="p-3">{{ t('legacyFamily') }}</th><th class="p-3">{{ t('legacyStatus') }}</th>
              <th class="p-3">{{ t('legacyDetails') }}</th>
            </tr></thead>
            <tbody>
              <tr v-for="row in filtered" :key="row.id" class="border-t align-top">
                <td class="whitespace-nowrap p-3 font-mono">{{ row.id }}</td>
                <td class="p-3">{{ row.route }}</td><td class="p-3">{{ row.family }}</td>
                <td class="p-3 font-medium">{{ row.status }}</td>
                <td class="p-3 text-xs">
                  <p>{{ row.entity }} · {{ row.queryName || row.templateName }}</p>
                  <p v-for="reason in row.reasons" :key="reason" class="mt-1 text-amber-800">{{ reason }}</p>
                  <p v-if="row.metadataVersion !== null" class="mt-1 text-slate-500">metadata v{{ row.metadataVersion }}</p>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <p class="text-xs text-slate-600">{{ t('legacyHistorical') }}: {{ history.length }} · {{ preview.sourceCommit }}</p>
      </template>
    </template>
  </section>
</template>
