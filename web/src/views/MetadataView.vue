<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
type Column = { logicalName: string; type: string; canRead: boolean; canCreate: boolean; canUpdate: boolean; isCalculated: boolean; choices: Array<{ value: number; labels: Record<string,string> }>; labels: Record<string,string> }
type Table = { logicalName: string; entitySetName: string; primaryIdAttribute: string; labels: Record<string,string>; columns: Column[]; relationships: Array<{schemaName: string; kind: string}> }
type Snapshot = { version: number; capturedAt: string; catalog: { tables: Table[] } }
type FieldEdit = { enabled: boolean; canRead: boolean; canCreate: boolean; canUpdate: boolean }
type Policy = { entities: Array<{logicalName: string; enabled: boolean}>; fields: Array<{entityName: string;fieldName: string;enabled: boolean;canRead: boolean;canCreate: boolean;canUpdate: boolean}> }
type Diff = {addedEntities:string[];removedEntities:string[];changedEntities:string[];addedFields:string[];removedFields:string[];changedFields:string[]}
const { t } = useI18n()
const csrf = ref('')
const connections = ref<Array<{code:string}>>([])
const selected = ref('')
const snapshot = ref<Snapshot|null>(null)
const policies = ref<Policy>({entities:[],fields:[]})
const diff = ref<Diff|null>(null)
const edits = ref<Record<string,FieldEdit>>({})
const expanded = ref('')
const busy = ref(false)
const info = ref('')
const authenticated = ref(false)
const schedule = ref({enabled:false,intervalMinutes:1440})
function key(e:string,f:string) { return e + '.' + f }
function enabledEntity(name:string) { return policies.value.entities.find(e=>e.logicalName===name)?.enabled ?? false }
function path(part='') { return '/api/admin/v1/connections/' + encodeURIComponent(selected.value) + '/metadata' + part }
async function api(url:string,method='GET',body?:object): Promise<unknown> {
  const response=await fetch(url,{method,credentials:'same-origin',cache:'no-store',
    headers:{...(body?{'Content-Type':'application/json'}:{}),
      ...(method!=='GET'?{'X-CSRF-TOKEN':csrf.value}:{})},
    ...(body?{body:JSON.stringify(body)}:{})})
  if(!response.ok) throw new Error('HTTP '+response.status)
  return response.status===204 ? null : response.json()
}
async function run(task:()=>Promise<void>) {
  busy.value=true; info.value=''
  try { await task() } catch(error) { info.value=error instanceof Error?error.message:t('error') }
  finally {busy.value=false}
}
async function load() {
  snapshot.value=null;diff.value=null;edits.value={}
  if(!selected.value)return
  const results=await Promise.all([
    api(path()).catch(()=>null),
    api(path('/policies')),
    api(path('/diff')).catch(()=>null),
    api(path('/schedule')),
  ])
  snapshot.value=results[0] as Snapshot|null
  policies.value=results[1] as Policy
  diff.value=results[2] as Diff|null
  const s=results[3] as {enabled:boolean;intervalMinutes:number}
  schedule.value={enabled:s.enabled,intervalMinutes:s.intervalMinutes}
  for(const table of snapshot.value?.catalog.tables??[])
    for(const field of table.columns) {
      const saved=policies.value.fields.find(p=>p.entityName===table.logicalName&&p.fieldName===field.logicalName)
      edits.value[key(table.logicalName,field.logicalName)] = saved
        ? {enabled:saved.enabled,canRead:saved.canRead,canCreate:saved.canCreate,canUpdate:saved.canUpdate}
        : {enabled:false,canRead:false,canCreate:false,canUpdate:false}
    }
}
async function refresh() { await run(async()=>{await api(path('/refresh'),'POST');await load();info.value=t('metadataRefreshDone')}) }
async function toggle(table:Table) { await run(async()=>{
  await api(path('/entities/'+encodeURIComponent(table.logicalName)+'/enabled'),'PUT',{enabled:!enabledEntity(table.logicalName)})
  await load();info.value=t('saved')
})}
async function saveField(table:Table,field:Column) { await run(async()=>{
  await api(path('/entities/'+encodeURIComponent(table.logicalName)+'/fields/'+encodeURIComponent(field.logicalName)+'/policy'),
    'PUT',edits.value[key(table.logicalName,field.logicalName)])
  await load();info.value=t('saved')
})}
async function saveSchedule(){await run(async()=>{
  await api(path('/schedule'),'PUT',schedule.value);await load();info.value=t('saved')
})}
onMounted(async()=>{
  try {
    const response=await fetch('/auth/session',{credentials:'same-origin',cache:'no-store'})
    if(!response.ok)return
    const session=await response.json() as {csrfToken:string}
    csrf.value=session.csrfToken
    connections.value=await api('/api/admin/v1/connections') as Array<{code:string}>
    authenticated.value=true
  } catch {authenticated.value=false}
})
</script>

<template>
  <section class="space-y-6">
    <h2 class="text-2xl font-semibold">{{t('metadataTitle')}}</h2>
    <p class="text-sm text-slate-600">{{t('metadataIntro')}}</p>
    <p v-if="!authenticated">{{t('signInRequired')}} <a href="/auth/login" class="underline">{{t('login')}}</a></p>
    <template v-else>
      <label class="block max-w-xl text-sm">{{t('connections')}}
        <select v-model="selected" class="mt-2 block w-full rounded border p-2" @change="run(load)">
          <option value="">{{t('selectConnection')}}</option>
          <option v-for="c in connections" :key="c.code" :value="c.code">{{c.code}}</option>
        </select>
      </label>
      <p v-if="info" role="status" aria-live="polite">{{info}}</p>
      <template v-if="selected">
        <div class="flex items-center gap-4 rounded border bg-white p-4">
          <button :disabled="busy" class="rounded bg-slate-900 px-4 py-2 text-white" @click="refresh">{{t('refreshMetadata')}}</button>
          <span class="text-sm">{{snapshot ? t('snapshotVersion')+' '+snapshot.version+' · '+snapshot.capturedAt : t('noMetadata')}}</span>
        </div>
        <form class="flex flex-wrap items-center gap-3 rounded border bg-white p-4" @submit.prevent="saveSchedule">
          <label class="text-sm">{{t('refreshInterval')}}
            <input v-model.number="schedule.intervalMinutes" type="number" min="60" max="10080" required class="ml-2 w-24 rounded border p-2">
          </label>
          <label class="text-sm"><input v-model="schedule.enabled" type="checkbox" class="mr-2">{{t('scheduledRefresh')}}</label>
          <button :disabled="busy" class="rounded border px-3 py-2">{{t('save')}}</button>
        </form>
        <section v-if="diff" class="space-y-2 rounded border bg-white p-4 text-sm">
          <h3 class="font-semibold">{{t('metadataChanges')}}</h3>
          <p>{{t('added')}}: {{diff.addedEntities.concat(diff.addedFields).join(', ')||'—'}}</p>
          <p>{{t('removed')}}: {{diff.removedEntities.concat(diff.removedFields).join(', ')||'—'}}</p>
          <p>{{t('changed')}}: {{diff.changedEntities.concat(diff.changedFields).join(', ')||'—'}}</p>
        </section>
        <p v-if="snapshot" class="text-sm text-slate-600">{{t('metadataDenyDefault')}}</p>
        <article v-for="table in snapshot?.catalog.tables??[]" :key="table.logicalName" class="rounded border bg-white p-4">
          <div class="flex flex-wrap items-center justify-between gap-3">
            <div>
              <h3 class="font-semibold">{{table.labels['3082']||table.labels['1033']||table.logicalName}}</h3>
              <p class="text-xs text-slate-600">{{table.logicalName}} · {{table.entitySetName}} · PK {{table.primaryIdAttribute}}</p>
              <p class="text-xs text-slate-600">{{table.columns.length}} {{t('fields')}} · {{table.relationships.length}} {{t('metadataRelationships')}}</p>
            </div>
            <div class="flex gap-2">
              <button :disabled="busy" class="rounded border px-3 py-2 text-sm" @click="toggle(table)">
                {{enabledEntity(table.logicalName)?t('disable'):t('enable')}}
              </button>
              <button class="rounded border px-3 py-2 text-sm" @click="expanded=expanded===table.logicalName?'':table.logicalName">
                {{t('details')}}
              </button>
            </div>
          </div>
          <div v-if="expanded===table.logicalName" class="mt-4 space-y-3">
            <p v-for="relation in table.relationships" :key="relation.schemaName" class="text-xs">
              {{relation.kind}}: {{relation.schemaName}}
            </p>
            <div v-for="field in table.columns" :key="field.logicalName" class="rounded border p-3">
              <div class="flex flex-wrap items-center justify-between gap-2">
                <div>
                  <strong class="text-sm">{{field.labels['3082']||field.labels['1033']||field.logicalName}}</strong>
                  <p class="text-xs text-slate-600">{{field.logicalName}} · {{field.type}}</p>
                  <p v-if="field.isCalculated" class="text-xs font-semibold">{{t('metadataCalculated')}}</p>
                  <p v-for="option in field.choices" :key="option.value" class="text-xs">
                    {{option.value}}: {{option.labels['3082']||option.labels['1033']||'—'}}
                  </p>
                </div>
                <div v-if="enabledEntity(table.logicalName)&&edits[key(table.logicalName,field.logicalName)]" class="flex flex-wrap items-center gap-2 text-xs">
                  <label><input v-model="edits[key(table.logicalName,field.logicalName)].enabled" type="checkbox"> {{t('enable')}}</label>
                  <label><input v-model="edits[key(table.logicalName,field.logicalName)].canRead" :disabled="!field.canRead" type="checkbox"> read</label>
                  <label><input v-model="edits[key(table.logicalName,field.logicalName)].canCreate" :disabled="!field.canCreate" type="checkbox"> create</label>
                  <label><input v-model="edits[key(table.logicalName,field.logicalName)].canUpdate" :disabled="!field.canUpdate" type="checkbox"> update</label>
                  <button :disabled="busy" class="rounded border px-3 py-1" @click="saveField(table,field)">{{t('save')}}</button>
                </div>
              </div>
            </div>
          </div>
        </article>
      </template>
    </template>
  </section>
</template>
