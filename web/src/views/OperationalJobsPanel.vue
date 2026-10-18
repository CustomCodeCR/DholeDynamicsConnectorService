<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAdminApi } from '../lib/adminApi'

type DurableJob = {
  id:string;kind:string;resourceKey:string;status:string;attempts:number;maxAttempts:number;
  pageCount:number;lastErrorCode:string|null;connectionId:string;createdAt:string
}
type SyncSettings = {entityName:string;enabled:boolean;mode:'delta'|'watermark';
  fallbackToWatermark:boolean;intervalMinutes:number}
type JobStats = {pending:number;running:number;dead:number;completed:number;
  unacknowledgedAlerts:number;changes24h:number;staleLeases:number}
type Alert = {id:string;connectionId:string;jobId:string|null;code:string;createdAt:string;acknowledgedAt:string|null}
type Checkpoint = {entity:string;mode:string;version:number;pages:number;changes:number;
  resumable:boolean;deltaReady:boolean;lastSuccessAt:string|null}
const props=defineProps<{connectionCode:string}>()
const api=useAdminApi()
const {t}=useI18n()
const authorized=ref(false)
const admin=ref(false)
const tasks=ref<DurableJob[]>([])
const stats=ref<JobStats|null>(null)
const alerts=ref<Alert[]>([])
const checkpoints=ref<Checkpoint[]>([])
const configs=ref<SyncSettings[]>([])
const entities=ref<string[]>([])
const selectedEntity=ref('')
const kind=ref('delta_sync')
const choice=ref<'delta'|'watermark'>('delta')
const fallback=ref(false)
const interval=ref(60)
const enabled=ref(false)
const confirm=ref(false)
const evidence=ref('')
const busyId=ref('')
const retention=ref({jobDays:30,observationDays:30,changeDays:30})
const alertVisible=ref(false)
const permissionWarning=ref('')

async function load(){
  if(!authorized.value)return
  await api.run(async()=>{
    const suffix=props.connectionCode?'?connectionCode='+encodeURIComponent(props.connectionCode):''
    const [items,overview,entries,policy]=await Promise.all([
      api.request<DurableJob[]>('/api/admin/v1/operations/jobs'+suffix),
      api.request<JobStats>('/api/admin/v1/operations/stats'),
      api.request<Alert[]>('/api/admin/v1/operations/alerts'+suffix),
      api.request<typeof retention.value>('/api/admin/v1/operations/retention')
    ])
    tasks.value=items;stats.value=overview;alerts.value=entries;retention.value=policy
    checkpoints.value=[];configs.value=[];entities.value=[]
    if(props.connectionCode){
      const encoded=encodeURIComponent(props.connectionCode)
      const [current,states,catalog]=await Promise.all([
        api.request<SyncSettings[]>('/api/admin/v1/operations/sync/'+encoded),
        api.request<Checkpoint[]>('/api/admin/v1/operations/sync/'+encoded+'/checkpoints'),
        api.loadCatalog(props.connectionCode)
      ])
      configs.value=current;checkpoints.value=states
      entities.value=catalog.entities.filter(e=>catalog.policies.fields.some(f=>
        f.entityName===e.logicalName && f.fieldName===e.primaryIdAttribute &&
        f.enabled && f.canRead)).map(x=>x.logicalName)
      if(!selectedEntity.value || !entities.value.includes(selectedEntity.value))
        selectedEntity.value=entities.value[0]??''
      selectConfiguration()
    }
  })
}

function selectConfiguration(){
  const selected=configs.value.find(x=>x.entityName===selectedEntity.value)
  choice.value=selected?.mode??'delta'
  fallback.value=selected?.fallbackToWatermark??false
  interval.value=selected?.intervalMinutes??60
  enabled.value=selected?.enabled??false
}
function newDedupe(){return 'admin-'+crypto.randomUUID().replaceAll('-','')}
async function enqueue(){
  if(!props.connectionCode||!admin.value)return
  if(kind.value==='delta_sync'&&!selectedEntity.value)return
  if(kind.value==='legacy_import'&&!confirm.value){permissionWarning.value=t('opsConfirmImport');return}
  await api.run(async()=>{
    await api.request('/api/admin/v1/operations/jobs/'+encodeURIComponent(props.connectionCode),
      'POST',{kind:kind.value,entityName:kind.value==='delta_sync'?selectedEntity.value:null,
        dedupeKey:newDedupe(),confirm:confirm.value})
    confirm.value=false
    api.message.value=t('opsQueued')
    await load()
  })
}
async function configure(){
  if(!props.connectionCode||!selectedEntity.value||!admin.value)return
  await api.run(async()=>{
    await api.request('/api/admin/v1/operations/sync/'+encodeURIComponent(props.connectionCode),
      'PUT',{entityName:selectedEntity.value,enabled:enabled.value,mode:choice.value,
        fallbackToWatermark:fallback.value,intervalMinutes:interval.value})
    api.message.value=t('saved')
    await load()
  })
}
async function replay(job:DurableJob){
  if(!props.connectionCode||!admin.value||!confirm.value||evidence.value.trim().length<15)return
  busyId.value=job.id
  await api.run(async()=>{
    await api.request('/api/admin/v1/operations/jobs/'+encodeURIComponent(props.connectionCode)+'/'+
      encodeURIComponent(job.id)+'/replay','POST',
      {confirm:true,reason:evidence.value.trim()})
    evidence.value='';confirm.value=false
    api.message.value=t('opsQueued')
    await load()
  })
  busyId.value=''
}
async function acknowledge(alert:Alert){
  if(!admin.value)return
  await api.run(async()=>{
    await api.request('/api/admin/v1/operations/alerts/'+encodeURIComponent(alert.id)+'/ack','POST')
    await load()
  })
}
async function saveRetention(){
  if(!admin.value)return
  await api.run(async()=>{
    await api.request('/api/admin/v1/operations/retention','PUT',retention.value)
    api.message.value=t('saved')
    await load()
  })
}
async function exportAudit(){
  if(!admin.value)return
  await api.run(async()=>{
    const suffix=props.connectionCode?'?connectionCode='+encodeURIComponent(props.connectionCode):''
    const response=await fetch('/api/admin/v1/operations/audit/export'+suffix,
      {credentials:'same-origin',cache:'no-store'})
    if(!response.ok)throw new Error(t('opsAuditDenied'))
    const blob=await response.blob()
    const url=URL.createObjectURL(blob)
    const element=document.createElement('a')
    element.href=url;element.download='connector-audit-sanitized.csv'
    document.body.append(element);element.click();element.remove()
    URL.revokeObjectURL(url)
  })
}
watch(()=>props.connectionCode,()=>{selectedEntity.value='';void load()})
watch(selectedEntity,selectConfiguration)
onMounted(async()=>{
  authorized.value=await api.loadSession()
  admin.value=!!api.session.value?.securityAdmin
  if(authorized.value)await load()
})
</script>

<template>
  <section class="space-y-4 rounded-xl border bg-white p-4">
    <h3 class="font-semibold">{{t('opsTitle')}}</h3>
    <p class="text-sm text-slate-600">{{t('opsIntro')}}</p>
    <p v-if="api.error.value" role="alert" class="text-red-700">{{api.error.value}}</p>
    <p v-if="api.message.value" role="status">{{api.message.value}}</p>
    <p v-if="permissionWarning" role="alert">{{permissionWarning}}</p>
    <div v-if="stats" class="grid grid-cols-2 gap-2 text-sm sm:grid-cols-3">
      <span>{{t('opsPending')}}: {{stats.pending}}</span>
      <span>{{t('opsRunning')}}: {{stats.running}}</span>
      <span>{{t('opsDead')}}: {{stats.dead}}</span>
      <span>{{t('opsDone')}}: {{stats.completed}}</span>
      <span>{{t('opsAlerts')}}: {{stats.unacknowledgedAlerts}}</span>
      <span>{{t('opsChanges')}} (24 h): {{stats.changes24h}}</span>
    </div>
    <div class="flex flex-wrap items-center gap-2">
      <button class="rounded border px-3 py-2 text-sm" :disabled="api.busy.value" @click="load">{{t('refresh')}}</button>
      <button v-if="admin" class="rounded border px-3 py-2 text-sm" @click="exportAudit">{{t('opsExportAudit')}}</button>
      <label class="flex items-center gap-2 text-sm">
        <input v-model="alertVisible" type="checkbox">{{t('opsShowAlerts')}}
      </label>
    </div>
    <div v-if="alertVisible" class="space-y-2">
      <div v-for="alert in alerts" :key="alert.id" class="flex items-center justify-between border-t py-2 text-sm">
        <span>{{alert.code}} · {{new Date(alert.createdAt).toLocaleString()}}</span>
        <button v-if="admin && !alert.acknowledgedAt" class="underline" @click="acknowledge(alert)">{{t('opsAcknowledge')}}</button>
      </div>
    </div>
    <div v-if="props.connectionCode && admin" class="space-y-3 rounded-lg border p-3">
      <h4 class="font-semibold">{{t('opsConfigure')}}</h4>
      <div class="flex flex-wrap gap-3">
        <label class="text-sm">{{t('templateEntity')}}
          <select v-model="selectedEntity" class="block rounded border p-2">
            <option v-for="entity in entities" :key="entity" :value="entity">{{entity}}</option>
          </select>
        </label>
        <label class="text-sm">{{t('opsMode')}}
          <select v-model="choice" class="block rounded border p-2">
            <option value="delta">odata.track-changes</option>
            <option value="watermark">modifiedon (watermark)</option>
          </select>
        </label>
        <label class="text-sm">{{t('opsInterval')}}
          <input v-model.number="interval" type="number" min="15" max="10080" class="block w-28 rounded border p-2">
        </label>
      </div>
      <label class="flex gap-2 text-sm"><input v-model="fallback" type="checkbox">{{t('opsFallback')}}</label>
      <label class="flex gap-2 text-sm"><input v-model="enabled" type="checkbox">{{t('enabled')}}</label>
      <button :disabled="!selectedEntity || api.busy.value" class="rounded bg-slate-900 px-3 py-2 text-sm text-white"
        @click="configure">{{t('save')}}</button>
      <h4 class="font-semibold">{{t('opsQueueTitle')}}</h4>
      <label class="text-sm">{{t('operation')}}
        <select v-model="kind" class="ml-2 rounded border p-2">
          <option value="delta_sync">delta_sync</option>
          <option value="metadata_refresh">metadata_refresh</option>
          <option value="legacy_preview">legacy_preview</option>
          <option value="legacy_import">legacy_import</option>
          <option value="reconcile_scan">reconcile_scan</option>
        </select>
      </label>
      <label v-if="kind==='legacy_import'" class="flex gap-2 text-sm">
        <input v-model="confirm" type="checkbox">{{t('opsConfirmImport')}}
      </label>
      <button :disabled="api.busy.value || kind==='delta_sync'&&!selectedEntity" class="rounded border px-3 py-2 text-sm"
        @click="enqueue">{{t('opsQueue')}}</button>
    </div>
    <div v-if="props.connectionCode" class="overflow-x-auto">
      <h4 class="my-2 font-semibold">{{t('opsCheckpoints')}}</h4>
      <table v-if="checkpoints.length" class="w-full text-left text-sm">
        <thead><tr><th>{{t('templateEntity')}}</th><th>{{t('opsMode')}}</th><th>{{t('opsPages')}}</th><th>{{t('opsChanges')}}</th><th>{{t('opsResume')}}</th></tr></thead>
        <tbody><tr v-for="state in checkpoints" :key="state.entity" class="border-t">
          <td class="py-2">{{state.entity}}</td><td>{{state.mode}}</td><td>{{state.pages}}</td>
          <td>{{state.changes}}</td><td>{{state.resumable?t('enabled'):t('disabled')}}</td>
        </tr></tbody>
      </table>
    </div>
    <div class="overflow-x-auto">
      <h4 class="my-2 font-semibold">{{t('opsQueueTitle')}}</h4>
      <table v-if="tasks.length" class="w-full text-left text-sm">
        <thead><tr><th>{{t('operation')}}</th><th>{{t('uiState')}}</th><th>{{t('opsAttempts')}}</th><th>{{t('details')}}</th></tr></thead>
        <tbody><tr v-for="job in tasks" :key="job.id" class="border-t">
          <td class="py-2">{{job.kind}} · {{job.resourceKey}}</td>
          <td>{{job.status}}</td><td>{{job.attempts}} / {{job.maxAttempts}}</td>
          <td>{{job.lastErrorCode??'—'}}</td>
          <td><button v-if="admin && props.connectionCode && job.status==='dead'"
            class="underline" :disabled="api.busy.value || busyId===job.id"
            @click="busyId=job.id">{{t('opsReplay')}}</button></td>
        </tr></tbody>
      </table>
      <div v-if="busyId" class="space-y-2 rounded border p-3">
        <label class="block text-sm">{{t('uiReason')}}
          <textarea v-model="evidence" rows="2" minlength="15" class="w-full rounded border p-2"></textarea>
        </label>
        <label class="flex gap-2 text-sm"><input v-model="confirm" type="checkbox">{{t('opsReplayConfirm')}}</label>
        <button :disabled="!confirm || evidence.trim().length<15" class="rounded border px-3 py-2"
          @click="replay(tasks.find(j=>j.id===busyId)!)">{{t('opsReplay')}}</button>
        <button class="rounded border px-3 py-2" @click="busyId='';confirm=false">{{t('uiCancel')}}</button>
      </div>
    </div>
    <div v-if="admin" class="space-y-2 rounded-lg border p-3">
      <h4 class="font-semibold">{{t('opsRetention')}}</h4>
      <div class="flex flex-wrap gap-3 text-sm">
        <label>{{t('opsJobDays')}} <input v-model.number="retention.jobDays" type="number" min="7" max="365" class="block w-24 rounded border p-2"></label>
        <label>{{t('opsObservationDays')}} <input v-model.number="retention.observationDays" type="number" min="7" max="365" class="block w-24 rounded border p-2"></label>
        <label>{{t('opsChangeDays')}} <input v-model.number="retention.changeDays" type="number" min="7" max="365" class="block w-24 rounded border p-2"></label>
      </div>
      <button :disabled="api.busy.value" class="rounded border px-3 py-2" @click="saveRetention">{{t('save')}}</button>
    </div>
  </section>
</template>
