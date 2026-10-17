<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
type Connection = { code: string }
type Column = { logicalName: string; type: string }
type Table = { logicalName: string; columns: Column[] }
type Summary = { id: string; name: string; entityName: string; enabled: boolean;
  query: { select: string[]; filter?: {field?: string;op?: string;parameter?: string} } }
type Policy = { entities: Array<{logicalName:string;enabled:boolean}>;
  fields: Array<{entityName:string;fieldName:string;enabled:boolean;canRead:boolean}> }
const { t } = useI18n()
const csrf = ref('')
const connected = ref(false)
const connections = ref<Connection[]>([])
const selected = ref('')
const tables = ref<Table[]>([])
const policies = ref<Policy>({entities:[],fields:[]})
const saved = ref<Summary[]>([])
const busy = ref(false)
const notice = ref('')
const name = ref('')
const entity = ref('')
const select = ref<string[]>([])
const filter = ref('')
const operator = ref('eq')
const parameter = ref('value')

const enabledTables = () => tables.value.filter(e => policies.value.entities.some(p => p.logicalName === e.logicalName && p.enabled))
const allowedFields = () => tables.value.find(t => t.logicalName === entity.value)?.columns.filter(c =>
  policies.value.fields.some(p => p.entityName === entity.value && p.fieldName === c.logicalName && p.enabled && p.canRead)) ?? []
function paramType() {
  const type = allowedFields().find(c => c.logicalName === filter.value)?.type
  if (type === 'Uniqueidentifier' || type === 'Lookup') return 'guid'
  if (type === 'Integer' || type === 'Picklist' || type === 'State' || type === 'Status') return 'integer'
  if (type === 'Money' || type === 'Decimal' || type === 'Double') return 'decimal'
  if (type === 'Boolean') return 'boolean'
  if (type === 'DateTime') return 'datetime'
  return 'string'
}
function base() { return '/api/admin/v1/connections/'+encodeURIComponent(selected.value)+'/saved-queries' }
async function api(path:string,method='GET',body?:object):Promise<unknown> {
  const response=await fetch(path,{method,credentials:'same-origin',cache:'no-store',
    headers:{...(body?{'Content-Type':'application/json'}:{}),
      ...(method!=='GET'?{'X-CSRF-TOKEN':csrf.value}:{})},
    ...(body?{body:JSON.stringify(body)}:{})})
  if(!response.ok) throw new Error('HTTP '+response.status)
  return response.status===204 ? null : response.json()
}
async function run(action:()=>Promise<void>) {
  busy.value=true;notice.value=''
  try {await action()} catch(e){notice.value=e instanceof Error?e.message:t('error')}
  finally {busy.value=false}
}
async function load() {
  saved.value=[];tables.value=[];entity.value='';select.value=[];filter.value=''
  if(!selected.value)return
  const [s,metadata,p]=await Promise.all([
    api(base()) as Promise<Summary[]>,
    api('/api/admin/v1/connections/'+encodeURIComponent(selected.value)+'/metadata') as Promise<{catalog:{tables:Table[]}}>,
    api('/api/admin/v1/connections/'+encodeURIComponent(selected.value)+'/metadata/policies') as Promise<Policy>
  ])
  saved.value=s;tables.value=metadata.catalog.tables;policies.value=p
  entity.value=enabledTables()[0]?.logicalName??''
}
function selectEntity() {select.value=[];filter.value=''}
async function save() {
  await run(async()=>{
    if(!name.value||!entity.value||!select.value.length||!filter.value)throw new Error(t('savedQueryMissing'))
    const write={
      name:name.value,entityName:entity.value,
      query:{select:select.value,filter:{field:filter.value,op:operator.value,parameter:parameter.value},
        pageSize:25,count:false},
      parameters:[{name:parameter.value,type:paramType(),required:true}],
      enabled:false
    }
    await api(base(),'POST',write)
    saved.value=await api(base()) as Summary[]
    notice.value=t('savedQueryCreated')
  })
}
async function toggle(item:Summary) {
  await run(async()=>{
    await api(base()+'/'+encodeURIComponent(item.name)+'/enabled','PUT',{enabled:!item.enabled})
    saved.value=await api(base()) as Summary[]
    notice.value=t('saved')
  })
}
onMounted(async()=>{
  try{
    const auth=await fetch('/auth/session',{credentials:'same-origin',cache:'no-store'})
    if(!auth.ok)return
    csrf.value=(await auth.json() as {csrfToken:string}).csrfToken
    connections.value=await api('/api/admin/v1/connections') as Connection[]
    connected.value=true
  }catch{connected.value=false}
})
</script>

<template>
  <section class="space-y-5">
    <h2 class="text-2xl font-semibold">{{t('savedQueries')}}</h2>
    <p class="text-sm text-slate-600">{{t('savedQueryHelp')}}</p>
    <p v-if="!connected">{{t('signInRequired')}} <a href="/auth/login" class="underline">{{t('login')}}</a></p>
    <template v-else>
      <label class="block text-sm">{{t('selectConnection')}}
        <select v-model="selected" class="mt-1 block w-full max-w-lg rounded border p-2" @change="run(load)">
          <option value="">{{t('selectConnection')}}</option>
          <option v-for="c in connections" :key="c.code" :value="c.code">{{c.code}}</option>
        </select>
      </label>
      <p v-if="notice" role="status" aria-live="polite">{{notice}}</p>
      <template v-if="selected">
        <form class="space-y-4 rounded-xl border bg-white p-5" @submit.prevent="save">
          <h3 class="font-semibold">{{t('savedQueryCreate')}}</h3>
          <label class="block text-sm">{{t('savedQueryName')}}
            <input v-model="name" required pattern="[a-z][a-z0-9_-]*" maxlength="40" class="mt-1 block w-full rounded border p-2" placeholder="shipment_po">
          </label>
          <label class="block text-sm">{{t('savedQueryTable')}}
            <select v-model="entity" required class="mt-1 block w-full rounded border p-2" @change="selectEntity">
              <option v-for="table in enabledTables()" :key="table.logicalName" :value="table.logicalName">{{table.logicalName}}</option>
            </select>
          </label>
          <fieldset class="rounded border p-3">
            <legend class="text-sm font-medium">{{t('savedQuerySelect')}}</legend>
            <label v-for="field in allowedFields()" :key="field.logicalName" class="mr-4 inline-flex items-center gap-2 p-2 text-sm">
              <input v-model="select" type="checkbox" :value="field.logicalName">{{field.logicalName}}
            </label>
          </fieldset>
          <label class="block text-sm">{{t('savedQueryFilter')}}
            <select v-model="filter" required class="mt-1 block w-full rounded border p-2">
              <option value="">{{t('savedQueryFilter')}}</option>
              <option v-for="field in allowedFields()" :key="field.logicalName" :value="field.logicalName">{{field.logicalName}} — {{field.type}}</option>
            </select>
          </label>
          <div class="flex gap-3">
            <label class="flex-1 text-sm">{{t('operation')}}
              <select v-model="operator" class="mt-1 block w-full rounded border p-2">
                <option value="eq">equals</option>
                <option value="ne">not equal</option>
                <option v-if="paramType()==='string'" value="contains">contains</option>
                <option value="gt">greater than</option>
                <option value="lt">less than</option>
              </select>
            </label>
            <label class="flex-1 text-sm">{{t('savedQueryParameter')}}
              <input v-model="parameter" required pattern="[a-z][a-z0-9_]*" maxlength="40" class="mt-1 block w-full rounded border p-2">
            </label>
          </div>
          <p class="text-xs text-slate-600">{{t('savedQueryDisabled')}}</p>
          <button :disabled="busy" type="submit" class="rounded bg-slate-900 px-4 py-2 text-white">{{t('save')}}</button>
        </form>
        <section class="space-y-2 rounded-xl border bg-white p-5">
          <h3 class="font-semibold">{{t('savedQueries')}}</h3>
          <p v-if="saved.length===0" class="text-sm text-slate-600">{{t('savedQueryEmpty')}}</p>
          <div v-for="item in saved" :key="item.id" class="flex items-center justify-between gap-2 rounded border p-3">
            <div>
              <strong class="text-sm">{{item.name}}</strong>
              <p class="text-xs text-slate-600">{{item.entityName}} · {{item.query.select.join(', ')}}</p>
              <p class="text-xs">{{item.enabled?t('enabled'):t('disabled')}}</p>
            </div>
            <button :disabled="busy" class="rounded border px-3 py-2 text-sm" @click="toggle(item)">
              {{item.enabled?t('disable'):t('enable')}}
            </button>
          </div>
        </section>
      </template>
    </template>
  </section>
</template>
