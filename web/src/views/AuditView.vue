<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAdminApi, type Connection } from '../lib/adminApi'
type Event = {id:string;connectionId:string|null;actor:string;action:string;at:string}
const {t}=useI18n()
const api=useAdminApi()
const authenticated=ref(false)
const connections=ref<Connection[]>([])
const code=ref('')
const events=ref<Event[]>([])
const limit=ref(50)
async function load(){
  await api.run(async()=>{
    events.value=await api.request<Event[]>('/api/admin/v1/console/audit?limit='+limit.value+
      (code.value?'&connectionCode='+encodeURIComponent(code.value):''))
  })
}
onMounted(async()=>{
  authenticated.value=await api.loadSession()
  if(authenticated.value){connections.value=await api.request<Connection[]>('/api/admin/v1/connections');await load()}
})
</script>
<template>
  <section class="space-y-5">
    <h2 class="text-2xl font-semibold">{{t('uiAuditTitle')}}</h2>
    <p class="text-sm text-slate-600">{{t('uiAuditIntro')}}</p>
    <p v-if="!authenticated">{{t('signInRequired')}} <a href="/auth/login" class="underline">{{t('login')}}</a></p>
    <template v-else>
      <div class="flex flex-wrap items-end gap-3">
        <label class="text-sm">{{t('selectConnection')}}
          <select v-model="code" class="mt-1 block rounded border p-2" @change="load">
            <option value="">{{t('uiAllConnections')}}</option>
            <option v-for="c in connections" :key="c.id" :value="c.code">{{c.code}}</option>
          </select>
        </label>
        <label class="text-sm">{{t('uiLimit')}}
          <select v-model.number="limit" class="mt-1 block rounded border p-2" @change="load">
            <option :value="20">20</option><option :value="50">50</option><option :value="100">100</option>
          </select>
        </label>
        <button class="rounded border px-4 py-2" :disabled="api.busy.value" @click="load">{{t('refresh')}}</button>
      </div>
      <p v-if="api.error.value" role="alert" class="text-red-700">{{api.error.value}}</p>
      <div class="overflow-x-auto rounded-xl border bg-white">
        <table class="w-full text-left text-sm">
          <thead class="bg-slate-100"><tr><th class="p-3">{{t('uiStarted')}}</th><th class="p-3">{{t('selectConnection')}}</th>
            <th class="p-3">{{t('uiAction')}}</th><th class="p-3">{{t('uiActor')}}</th><th class="p-3">ID</th></tr></thead>
          <tbody><tr v-for="event in events" :key="event.id" class="border-t">
            <td class="p-3">{{new Date(event.at).toLocaleString()}}</td>
            <td class="p-3">{{connections.find(c=>c.id===event.connectionId)?.code??'—'}}</td>
            <td class="p-3">{{event.action}}</td><td class="p-3">{{event.actor}}</td>
            <td class="p-3 font-mono text-xs">{{event.id}}</td>
          </tr></tbody>
        </table>
      </div>
      <p class="text-xs text-slate-600">{{t('uiAuditLimits')}}</p>
    </template>
  </section>
</template>
