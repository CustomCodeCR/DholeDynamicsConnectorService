<script setup lang="ts">
import { onMounted, ref } from 'vue'
import OperationalJobsPanel from './OperationalJobsPanel.vue'
import { useI18n } from 'vue-i18n'
import { useAdminApi, type Connection } from '../lib/adminApi'
type Job = { id:string;connectionId:string;entity:string;operation:string;state:string;
  startedAt:string;finishedAt:string|null;httpStatus:number|null;recordId:string|null;kind:string }
type Schedule = { connectionId:string;code:string;enabled:boolean;intervalMinutes:number;nextRunAt:string }
const { t }=useI18n()
const api=useAdminApi()
const authenticated=ref(false)
const connections=ref<Connection[]>([])
const code=ref('')
const jobs=ref<Job[]>([])
const schedules=ref<Schedule[]>([])
const selected=ref<Job|null>(null)
const reason=ref('')
const resolution=ref('found')
const confirmedRecord=ref('')
const confirm=ref(false)
async function load(){
  await api.run(async()=>{
    const suffix=code.value?'?connectionCode='+encodeURIComponent(code.value):''
    const [items,refreshes]=await Promise.all([
      api.request<Job[]>('/api/admin/v1/console/jobs'+suffix),
      api.request<Schedule[]>('/api/admin/v1/console/schedules'+suffix)
    ])
    jobs.value=items;schedules.value=refreshes
  })
}
async function retryMetadata(schedule:Schedule){
  await api.run(async()=>{
    await api.request('/api/admin/v1/connections/'+encodeURIComponent(schedule.code)+'/metadata/refresh','POST')
    api.message.value=t('uiMetadataRetried')
    await load()
  })
}
async function reconcile(){
  await api.run(async()=>{
    if(!selected.value || !api.session.value?.securityAdmin || !confirm.value ||
      reason.value.trim().length<10 || resolution.value==='found' && !confirmedRecord.value)
      throw new Error(t('uiConfirmReconcile'))
    const connection=connections.value.find(c=>c.id===selected.value!.connectionId)
    if(!connection)throw new Error(t('uiConnectionNotFound'))
    await api.request('/api/admin/v1/connections/'+encodeURIComponent(connection.code)+
      '/write-operations/'+encodeURIComponent(selected.value!.id)+'/reconcile','POST',{
        recordId:resolution.value==='found'?confirmedRecord.value:null,
        confirmedNotApplied:resolution.value==='missing',
        reason:reason.value.trim()
      })
    selected.value=null;confirm.value=false;reason.value='';confirmedRecord.value=''
    api.message.value=t('uiReconciled')
    await load()
  })
}
onMounted(async()=>{
  authenticated.value=await api.loadSession()
  if(authenticated.value){connections.value=await api.request<Connection[]>('/api/admin/v1/connections');await load()}
})
</script>

<template>
  <section class="space-y-5">
    <h2 class="text-2xl font-semibold">{{t('uiJobsTitle')}}</h2>
    <p class="text-sm text-slate-600">{{t('uiJobsIntro')}}</p>
    <p v-if="!authenticated">{{t('signInRequired')}} <a href="/auth/login" class="underline">{{t('login')}}</a></p>
    <template v-else>
      <div class="flex flex-wrap items-end gap-3">
        <label class="text-sm">{{t('selectConnection')}}
          <select v-model="code" class="mt-1 block rounded border p-2" @change="load">
            <option value="">{{t('uiAllConnections')}}</option>
            <option v-for="c in connections" :key="c.id" :value="c.code">{{c.code}}</option>
          </select>
        </label>
        <button :disabled="api.busy.value" class="rounded border px-4 py-2" @click="load">{{t('refresh')}}</button>
      </div>
      <p v-if="api.error.value" role="alert" class="text-red-700">{{api.error.value}}</p>
      <p v-if="api.message.value" role="status">{{api.message.value}}</p>
      <OperationalJobsPanel :connection-code="code" />
      <section class="rounded-xl border bg-white p-4">
        <h3 class="mb-3 font-semibold">{{t('uiScheduledRefresh')}}</h3>
        <p v-if="!schedules.length" class="text-sm text-slate-600">{{t('uiNoJobs')}}</p>
        <div v-for="s in schedules" :key="s.connectionId" class="flex flex-wrap items-center justify-between gap-3 border-t py-3 text-sm">
          <span>{{s.code}} · {{s.enabled?t('enabled'):t('disabled')}} · {{s.intervalMinutes}} min · {{new Date(s.nextRunAt).toLocaleString()}}</span>
          <button :disabled="api.busy.value" class="rounded border px-3 py-1" @click="retryMetadata(s)">{{t('uiRefreshNow')}}</button>
        </div>
      </section>
      <section class="rounded-xl border bg-white p-4">
        <h3 class="mb-3 font-semibold">{{t('uiWriteHistory')}}</h3>
        <p v-if="!jobs.length" class="text-sm text-slate-600">{{t('uiNoJobs')}}</p>
        <div class="overflow-x-auto">
          <table v-if="jobs.length" class="w-full text-left text-sm">
            <thead><tr><th class="p-2">{{t('templateEntity')}}</th><th class="p-2">{{t('operation')}}</th>
              <th class="p-2">{{t('uiState')}}</th><th class="p-2">{{t('uiStarted')}}</th>
              <th class="p-2">{{t('uiLatency')}}</th><th class="p-2">{{t('details')}}</th></tr></thead>
            <tbody>
              <tr v-for="job in jobs" :key="job.id" class="border-t">
                <td class="p-2">{{job.entity}}</td><td class="p-2">{{job.operation}}</td>
                <td class="p-2">{{job.state}}</td><td class="p-2">{{new Date(job.startedAt).toLocaleString()}}</td>
                <td class="p-2">{{job.finishedAt?Math.max(0,Date.parse(job.finishedAt)-Date.parse(job.startedAt))+' ms':'—'}}</td>
                <td class="p-2"><button v-if="['ambiguous','reserved'].includes(job.state) && api.session.value?.securityAdmin"
                  class="underline" @click="selected=job">{{t('uiReconcile')}}</button>
                  <span v-else>{{job.id.slice(0,8)}}</span></td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
      <section v-if="selected" class="space-y-3 rounded-xl border border-amber-400 bg-white p-4">
        <h3 class="font-semibold">{{t('uiManualReconcile')}}</h3>
        <p class="text-sm">{{selected.id}} · {{selected.entity}} · {{selected.state}}</p>
        <p class="text-sm text-red-700">{{t('uiNoAutomaticRetries')}}</p>
        <label class="block text-sm">{{t('uiReconciliationResult')}}
          <select v-model="resolution" class="mt-1 block w-full rounded border p-2">
            <option value="found">{{t('uiRecordConfirmed')}}</option>
            <option value="missing">{{t('uiVerifiedNotApplied')}}</option>
          </select>
        </label>
        <label v-if="resolution==='found'" class="block text-sm">{{t('uiRecordGuid')}}
          <input v-model.trim="confirmedRecord" class="mt-1 block w-full rounded border p-2">
        </label>
        <label class="block text-sm">{{t('uiReason')}}
          <textarea v-model="reason" minlength="10" rows="3" class="mt-1 w-full rounded border p-2"></textarea>
        </label>
        <label class="flex gap-2 text-sm"><input v-model="confirm" type="checkbox">{{t('uiConfirmReconcile')}}</label>
        <div class="flex gap-3">
          <button :disabled="api.busy.value || !confirm" class="rounded bg-slate-900 px-3 py-2 text-white" @click="reconcile">{{t('uiReconcile')}}</button>
          <button class="rounded border px-3 py-2" @click="selected=null">{{t('uiCancel')}}</button>
        </div>
      </section>
    </template>
  </section>
</template>
