<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAdminApi } from '../lib/adminApi'
type Diagnostics = { environment:string; oidcConfigured:boolean; m2mConfigured:boolean;
  externalOrigin:string|null; metadataSchedulerEnabled:boolean; backupStatus:string; secretsExposed:boolean }
const {t,locale}=useI18n()
const api=useAdminApi()
const authenticated=ref(false)
const detail=ref<Diagnostics|null>(null)
const timezone=ref(Intl.DateTimeFormat().resolvedOptions().timeZone)
const timezones=['America/Costa_Rica','America/Panama','America/New_York','UTC']
async function reload(){
  await api.run(async()=>{
    detail.value=await api.request<Diagnostics>('/api/admin/v1/console/diagnostics')
  })
}
function changeLanguage(event:Event){locale.value=(event.target as HTMLSelectElement).value}
onMounted(async()=>{
  authenticated.value=await api.loadSession()
  if(authenticated.value)await reload()
})
</script>
<template>
  <section class="space-y-5">
    <h2 class="text-2xl font-semibold">{{t('uiSettingsTitle')}}</h2>
    <p class="text-sm text-slate-600">{{t('uiSettingsIntro')}}</p>
    <div class="grid gap-5 sm:grid-cols-2">
      <section class="space-y-3 rounded-xl border bg-white p-5">
        <h3 class="font-semibold">{{t('uiPresentation')}}</h3>
        <label class="block text-sm">{{t('uiLanguage')}}
          <select :value="locale" class="mt-1 block w-full rounded border p-2" @change="changeLanguage">
            <option value="es">Español</option><option value="en">English</option>
          </select>
        </label>
        <label class="block text-sm">{{t('uiTimeZone')}}
          <select v-model="timezone" class="mt-1 block w-full rounded border p-2">
            <option v-for="zone in timezones" :key="zone" :value="zone">{{zone}}</option>
          </select>
        </label>
        <p class="text-xs">{{t('uiTimeZoneNote')}}: {{new Date().toLocaleString(locale,{timeZone:timezone})}}</p>
      </section>
      <section class="space-y-3 rounded-xl border bg-white p-5">
        <h3 class="font-semibold">{{t('uiDiagnostics')}}</h3>
        <p v-if="!authenticated">{{t('signInRequired')}} <a href="/auth/login" class="underline">{{t('login')}}</a></p>
        <template v-else-if="detail">
          <p class="text-sm">Environment: {{detail.environment}}</p>
          <p class="text-sm">Admin OIDC: {{detail.oidcConfigured?t('enabled'):t('disabled')}}</p>
          <p class="text-sm">M2M OAuth: {{detail.m2mConfigured?t('enabled'):t('disabled')}}</p>
          <p class="text-sm">Scheduler: {{detail.metadataSchedulerEnabled?t('enabled'):t('disabled')}}</p>
          <p class="break-all text-sm">{{t('uiPublicOrigin')}}: {{detail.externalOrigin??'—'}}</p>
          <p class="text-sm">{{t('uiBackupStatus')}}: {{t('uiBackupNotTracked')}}</p>
          <p class="text-xs text-slate-600">{{t('uiSecretsNeverShown')}}</p>
          <button :disabled="api.busy.value" class="rounded border px-3 py-2" @click="reload">{{t('refresh')}}</button>
        </template>
        <p v-if="api.error.value" role="alert" class="text-sm text-red-700">{{api.error.value}}</p>
      </section>
    </div>
    <p class="text-sm">{{t('uiSettingsOperations')}}</p>
    <div class="flex flex-wrap gap-3 text-sm">
      <RouterLink to="/admin" class="rounded border px-3 py-2">{{t('connections')}}</RouterLink>
      <RouterLink to="/metadata" class="rounded border px-3 py-2">{{t('metadataTitle')}}</RouterLink>
      <RouterLink to="/jobs" class="rounded border px-3 py-2">{{t('uiJobsTitle')}}</RouterLink>
    </div>
  </section>
</template>
