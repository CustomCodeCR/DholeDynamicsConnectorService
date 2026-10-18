<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAdminApi, safeCsv, downloadCsv, type Connection, type Entity, type Policies,
  type Field, type RecordPage, type RecordDetail } from '../lib/adminApi'
const { t } = useI18n()
const api = useAdminApi()
const authenticated = ref(false)
const connections = ref<Connection[]>([])
const code = ref('')
const tables = ref<Entity[]>([])
const policies = ref<Policies>({entities:[],fields:[]})
const entityName = ref('')
const select = ref<string[]>([])
const filter = ref('')
const operator = ref('eq')
const filterValue = ref('')
const sort = ref('')
const descending = ref(false)
const pageSize = ref(25)
const page = ref<RecordPage | null>(null)
const accumulated = ref<Array<Record<string,unknown>>>([])
const detail = ref<RecordDetail | null>(null)
const confirmedExport = ref(false)
const selectedEntity = computed(() => tables.value.find(e => e.logicalName === entityName.value))
const readable = computed(() => selectedEntity.value
  ? api.allowedFields(selectedEntity.value,policies.value,'read') : [])
const filteredColumn = computed(() => readable.value.find(f => f.logicalName === filter.value))
function parseValue(field: Field, raw: string): unknown {
  if (['Integer','BigInt','Picklist','State','Status','Money','Decimal','Double'].includes(field.type)) {
    if (raw.trim()==='' || !Number.isFinite(Number(raw))) throw new Error(t('uiInvalidValue'))
    return Number(raw)
  }
  if (field.type === 'Boolean') return raw === 'true'
  return raw
}
function changeEntity() {
  const allowed = readable.value.map(f=>f.logicalName)
  select.value = selectedEntity.value?.primaryIdAttribute && allowed.includes(selectedEntity.value.primaryIdAttribute)
    ? [selectedEntity.value.primaryIdAttribute] : allowed.slice(0,2)
  filter.value='';filterValue.value='';sort.value='';page.value=null;accumulated.value=[];detail.value=null
}
async function loadConnection() {
  await api.run(async () => {
    const result = await api.loadCatalog(code.value)
    tables.value=result.entities;policies.value=result.policies
    entityName.value=tables.value[0]?.logicalName??''
    changeEntity()
  })
}
async function search(cursor?: string) {
  await api.run(async () => {
    if(!selectedEntity.value || !select.value.length)throw new Error(t('uiChooseColumns'))
    if(!cursor){page.value=null;accumulated.value=[];detail.value=null}
    let request: object = {connectionCode:code.value,cursor}
    if(!cursor) {
      const field = filteredColumn.value
      const val = field && filter.value ? (operator.value==='null' ? undefined : parseValue(field,filterValue.value)) : undefined
      request = {connectionCode:code.value,query:{
        select: select.value, pageSize: pageSize.value, count:true,
        ...(field ? {filter: {field:field.logicalName,op:operator.value,...(val===undefined?{}:{value:val})}} : {}),
        ...(sort.value?{order:[{field:sort.value,desc:descending.value}]}:{})
      }}
    }
    const response = await api.request<RecordPage>(
      '/api/admin/v1/console/records/'+encodeURIComponent(entityName.value)+'/search','POST',request)
    page.value=response
    accumulated.value=[...accumulated.value,...response.items].slice(0,500)
  })
}
async function openDetail(item: Record<string,unknown>) {
  await api.run(async()=>{
    const entity=selectedEntity.value
    const id=entity && item[entity.primaryIdAttribute]
    if(typeof id !== 'string')throw new Error(t('uiNoRecordId'))
    detail.value=await api.request<RecordDetail>(
      '/api/admin/v1/console/records/'+encodeURIComponent(entity!.logicalName)+'/'+encodeURIComponent(id)+
      '?connectionCode='+encodeURIComponent(code.value)+'&select='+encodeURIComponent(select.value.join(',')))
  })
}
function exportResults() {
  if(!page.value || !confirmedExport.value || !select.value.length)return
  downloadCsv(safeCsv(accumulated.value,select.value),
    'dataverse-'+entityName.value+'-'+new Date().toISOString().slice(0,10)+'.csv')
}
onMounted(async()=>{
  authenticated.value=await api.loadSession()
  if(authenticated.value)connections.value=await api.request<Connection[]>('/api/admin/v1/connections')
})
</script>

<template>
  <section class="space-y-5">
    <h2 class="text-2xl font-semibold">{{t('uiSearchTitle')}}</h2>
    <p class="text-sm text-slate-600">{{t('uiSearchIntro')}}</p>
    <p v-if="!authenticated">{{t('signInRequired')}} <a href="/auth/login" class="underline">{{t('login')}}</a></p>
    <p v-else-if="!api.session.value?.securityAdmin" role="alert">{{t('uiSecurityAdminOnly')}}</p>
    <template v-else>
      <label class="block text-sm">{{t('selectConnection')}}
        <select v-model="code" class="mt-1 w-full max-w-xl rounded border p-2" @change="loadConnection">
          <option value="">{{t('selectConnection')}}</option>
          <option v-for="c in connections" :key="c.id" :value="c.code">{{c.code}}</option>
        </select>
      </label>
      <div v-if="code && selectedEntity" class="space-y-4 rounded-xl border bg-white p-4">
        <label class="block text-sm">{{t('templateEntity')}}
          <select v-model="entityName" class="mt-1 w-full rounded border p-2" @change="changeEntity">
            <option v-for="e in tables" :key="e.logicalName" :value="e.logicalName">{{e.logicalName}}</option>
          </select>
        </label>
        <fieldset class="rounded border p-3">
          <legend class="text-sm font-semibold">{{t('uiColumns')}}</legend>
          <label v-for="f in readable" :key="f.logicalName" class="mr-4 inline-flex gap-2 py-2 text-sm">
            <input v-model="select" type="checkbox" :value="f.logicalName">{{f.logicalName}}
          </label>
        </fieldset>
        <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <label class="text-sm">{{t('uiFilterColumn')}}
            <select v-model="filter" class="mt-1 w-full rounded border p-2">
              <option value="">{{t('uiNoFilter')}}</option>
              <option v-for="f in readable" :key="f.logicalName" :value="f.logicalName">{{f.logicalName}}</option>
            </select>
          </label>
          <label class="text-sm">{{t('operation')}}
            <select v-model="operator" class="mt-1 w-full rounded border p-2">
              <option v-for="op in ['eq','ne','gt','ge','lt','le','contains','null']" :key="op" :value="op">{{op}}</option>
            </select>
          </label>
          <label class="text-sm">{{t('uiFilterValue')}}
            <select v-if="filteredColumn?.type === 'Boolean'" v-model="filterValue" class="mt-1 w-full rounded border p-2">
              <option value="true">true</option><option value="false">false</option>
            </select>
            <select v-else-if="filteredColumn?.choices?.length" v-model="filterValue" class="mt-1 w-full rounded border p-2">
              <option v-for="choice in filteredColumn.choices" :key="choice.value" :value="String(choice.value)">
                {{choice.value}} — {{Object.values(choice.labels)[0]??''}}
              </option>
            </select>
            <input v-else v-model="filterValue" :disabled="!filter || operator==='null'" class="mt-1 w-full rounded border p-2">
          </label>
          <label class="text-sm">{{t('uiSort')}}
            <select v-model="sort" class="mt-1 w-full rounded border p-2">
              <option value="">{{t('uiNoSort')}}</option>
              <option v-for="f in readable" :key="f.logicalName" :value="f.logicalName">{{f.logicalName}}</option>
            </select>
          </label>
        </div>
        <div class="flex flex-wrap items-center gap-4">
          <label class="text-sm">{{t('uiPageSize')}}
            <select v-model.number="pageSize" class="ml-2 rounded border p-2">
              <option :value="10">10</option><option :value="25">25</option><option :value="50">50</option>
            </select>
          </label>
          <label class="text-sm"><input v-model="descending" type="checkbox" class="mr-2">{{t('uiDescending')}}</label>
          <button :disabled="api.busy.value || !select.length" class="rounded bg-slate-900 px-4 py-2 text-white" @click="search()">{{t('uiSearch')}}</button>
        </div>
      </div>
      <p v-if="api.error.value" role="alert" class="text-red-700">{{api.error.value}}</p>
      <section v-if="page" class="space-y-3">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <p class="text-sm">{{t('uiPage')}} {{page.page}} · {{accumulated.length}} {{t('uiResults')}} · {{t('snapshotVersion')}} {{page.metadataVersion}}</p>
          <button :disabled="api.busy.value || !page.nextCursor" class="rounded border px-3 py-2" @click="search(page.nextCursor!)">{{t('uiNextPage')}}</button>
        </div>
        <div class="overflow-x-auto rounded-xl border bg-white">
          <table class="w-full text-left text-sm">
            <thead class="bg-slate-100"><tr><th v-for="field in select" :key="field" scope="col" class="p-3">{{field}}</th><th class="p-3">{{t('details')}}</th></tr></thead>
            <tbody><tr v-for="(item,index) in accumulated" :key="index" class="border-t">
              <td v-for="field in select" :key="field" class="max-w-xs truncate p-3">{{item[field]}}</td>
              <td class="p-3"><button class="underline" @click="openDetail(item)">{{t('details')}}</button></td>
            </tr></tbody>
          </table>
        </div>
        <p v-if="page.truncated" class="text-sm text-amber-800">{{t('uiPageLimit')}}</p>
        <label class="flex gap-2 text-sm"><input v-model="confirmedExport" type="checkbox">{{t('uiExportConfirm')}}</label>
        <button :disabled="!confirmedExport" class="rounded border px-3 py-2" @click="exportResults">{{t('uiExportCsv')}}</button>
        <pre v-if="detail" class="max-h-72 overflow-auto rounded bg-slate-100 p-4 text-xs">{{JSON.stringify(detail.item,null,2)}}</pre>
      </section>
    </template>
  </section>
</template>
