<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAdminApi, type Connection, type Entity, type Policies, type Field,
  type RecordDetail, type WriteResult } from '../lib/adminApi'

const { t }=useI18n()
const api=useAdminApi()
const authenticated=ref(false)
const connections=ref<Connection[]>([])
const code=ref('')
const entities=ref<Entity[]>([])
const policies=ref<Policies>({entities:[],fields:[]})
const entityName=ref('')
const mode=ref<'create'|'update'|'delete'>('create')
const recordId=ref('')
const etag=ref('')
const selection=ref<string[]>([])
const draft=ref<Record<string,string|boolean>>({})
const relatedIds=ref<Record<string,string>>({})
const confirmation=ref(false)
const confirmationText=ref('')
const reason=ref('')
const result=ref<WriteResult | null>(null)
const ambiguous=ref(false)
const entity=computed(()=>entities.value.find(e=>e.logicalName===entityName.value))
const fields=computed(()=>entity.value?api.allowedFields(entity.value,policies.value,mode.value==='create'?'create':'update'):[])
const relations=computed(()=>entity.value?.relationships.filter(r=>r.referencingNavigation &&
  r.referencingAttribute && fields.value.some(f=>f.logicalName===r.referencingAttribute))??[])
function reset() {
  selection.value=[];draft.value={};relatedIds.value={}
  recordId.value='';etag.value='';reason.value='';confirmation.value=false;confirmationText.value=''
  result.value=null;ambiguous.value=false
}
async function selectConnection(){
  await api.run(async()=>{
    const found=await api.loadCatalog(code.value)
    entities.value=found.entities;policies.value=found.policies
    entityName.value=entities.value[0]?.logicalName??''
    reset()
  })
}
function parse(field: Field, raw: string | boolean): unknown {
  if (field.type==='Boolean')return raw===true || raw==='true'
  if (['Integer','BigInt','Picklist','Status','State','Money','Decimal','Double'].includes(field.type)) {
    if(String(raw).trim()==='' || !Number.isFinite(Number(raw)))throw new Error(t('uiInvalidValue'))
    return Number(raw)
  }
  if (field.type==='MultiSelectPicklist')return String(raw).split(',').map(x=>Number(x.trim()))
  if (field.type==='DateTime') {
    const time=new Date(String(raw))
    if(Number.isNaN(time.getTime()))throw new Error(t('uiInvalidValue'))
    return time.toISOString()
  }
  return String(raw)
}
function payload(): Record<string,unknown> {
  const values:Record<string,unknown>={}
  for(const name of selection.value) {
    const field=fields.value.find(x=>x.logicalName===name)
    if (!field) throw new Error(t('uiFieldDenied'))
    if (['Lookup','Customer','Owner'].includes(field.type))continue
    values[name]=parse(field,draft.value[name]??'')
  }
  for(const relation of relations.value) {
    if(!relation.referencingAttribute || !selection.value.includes(relation.referencingAttribute))continue
    const id=relatedIds.value[relation.referencingNavigation!]??''
    if(!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id))
      throw new Error(t('uiInvalidGuid'))
    values[relation.referencingNavigation!+'@odata.bind']=id
  }
  if(Object.keys(values).length===0)throw new Error(t('uiChooseColumns'))
  return values
}
async function loadRecord() {
  await api.run(async()=>{
    if(!entity.value || !recordId.value)throw new Error(t('uiInvalidGuid'))
    const permitted=api.allowedFields(entity.value,policies.value,'read')
    if(!permitted.length)throw new Error(t('uiFieldDenied'))
    const response=await api.request<RecordDetail>(
      '/api/admin/v1/console/records/'+encodeURIComponent(entity.value.logicalName)+'/'+encodeURIComponent(recordId.value)+
      '?connectionCode='+encodeURIComponent(code.value)+'&select='+encodeURIComponent(permitted.map(f=>f.logicalName).slice(0,30).join(',')))
    etag.value=response.eTag??''
    for(const field of permitted) {
      const v=response.item[field.logicalName]
      if (v!==undefined && v!==null && !['Lookup','Customer','Owner'].includes(field.type))
        draft.value[field.logicalName]=typeof v==='boolean'?v:String(v)
    }
    api.message.value=t('uiLoadedEtag')
  })
}
async function save(){
  await api.run(async()=>{
    if(!entity.value||!code.value||ambiguous.value)throw new Error(t('uiWriteBlocked'))
    const path='/api/admin/v1/console/records/'+encodeURIComponent(entity.value.logicalName)
    let method='POST';let url=path;let body:object
    if(mode.value==='create')body={connectionCode:code.value,values:payload()}
    else if(mode.value==='update'){
      if(!etag.value||!recordId.value)throw new Error(t('uiEtagRequired'))
      method='PATCH';url+='/'+encodeURIComponent(recordId.value)
      body={connectionCode:code.value,values:payload()}
    } else {
      if(!confirmation.value||confirmationText.value!==t('uiDeletePhrase')||reason.value.trim().length<10)
        throw new Error(t('uiDeleteConfirmationRequired'))
      if(!recordId.value||!etag.value)throw new Error(t('uiEtagRequired'))
      method='DELETE';url+='/'+encodeURIComponent(recordId.value)
      body={connectionCode:code.value,confirm:true,reason:reason.value.trim()}
    }
    // New key for each explicit action. On ambiguous outcome disable further writes for this screen.
    const key='ui-'+crypto.randomUUID().replaceAll('-','')
    const res=await api.request<WriteResult>(url,method,body,{
      'Idempotency-Key':key,...(mode.value==='create'?{}:{'If-Match':etag.value}),
    })
    result.value=res
    ambiguous.value=res.state==='ambiguous'
    if (res.state==='ambiguous')api.error.value=t('uiWriteAmbiguous')
    else api.message.value=t('saved')+' · '+res.operationId
    if(res.state==='succeeded' && mode.value!=='create')etag.value=''
  })
}
onMounted(async()=>{
  authenticated.value=await api.loadSession()
  if(authenticated.value)connections.value=await api.request<Connection[]>('/api/admin/v1/connections')
})
</script>

<template>
  <section class="space-y-5">
    <h2 class="text-2xl font-semibold">{{t('uiEditorTitle')}}</h2>
    <p class="text-sm text-slate-600">{{t('uiEditorIntro')}}</p>
    <p v-if="!authenticated">{{t('signInRequired')}} <a class="underline" href="/auth/login">{{t('login')}}</a></p>
    <p v-else-if="!api.session.value?.securityAdmin" role="alert">{{t('uiSecurityAdminOnly')}}</p>
    <template v-else>
      <label class="block text-sm">{{t('selectConnection')}}
        <select v-model="code" class="mt-1 w-full max-w-xl rounded border p-2" @change="selectConnection">
          <option value="">{{t('selectConnection')}}</option>
          <option v-for="item in connections" :key="item.id" :value="item.code">{{item.code}}</option>
        </select>
      </label>
      <template v-if="code && entity">
        <div class="grid gap-3 rounded-xl border bg-white p-4 sm:grid-cols-2">
          <label class="text-sm">{{t('templateEntity')}}
            <select v-model="entityName" class="mt-1 w-full rounded border p-2" @change="reset">
              <option v-for="item in entities" :key="item.logicalName" :value="item.logicalName">{{item.logicalName}}</option>
            </select>
          </label>
          <label class="text-sm">{{t('operation')}}
            <select v-model="mode" class="mt-1 w-full rounded border p-2" @change="reset">
              <option value="create">{{t('uiCreate')}}</option><option value="update">{{t('uiUpdate')}}</option>
              <option value="delete">{{t('uiDelete')}}</option>
            </select>
          </label>
        </div>
        <div v-if="mode!=='create'" class="space-y-3 rounded-xl border bg-white p-4">
          <label class="block text-sm">Record GUID
            <input v-model.trim="recordId" required class="mt-1 block w-full rounded border p-2" placeholder="00000000-0000-0000-0000-000000000000">
          </label>
          <button :disabled="api.busy.value" class="rounded border px-3 py-2" @click="loadRecord">{{t('uiLoadRecord')}}</button>
          <label class="block text-sm">ETag (If-Match)
            <input v-model="etag" readonly class="mt-1 block w-full rounded border bg-slate-100 p-2">
          </label>
        </div>
        <fieldset v-if="mode!=='delete'" class="space-y-3 rounded-xl border bg-white p-4">
          <legend class="font-semibold">{{t('uiFieldsToWrite')}}</legend>
          <div v-for="field in fields" :key="field.logicalName" class="grid gap-3 border-b pb-3 sm:grid-cols-2">
            <label class="inline-flex items-center gap-2 text-sm">
              <input v-model="selection" type="checkbox" :value="field.logicalName">{{field.logicalName}}
              <small class="text-slate-500">{{field.type}}</small>
            </label>
            <template v-if="selection.includes(field.logicalName)">
              <select v-if="field.type==='Boolean'" v-model="draft[field.logicalName]" class="rounded border p-2">
                <option :value="true">true</option><option :value="false">false</option>
              </select>
              <select v-else-if="field.choices.length" v-model="draft[field.logicalName]" class="rounded border p-2">
                <option v-for="choice in field.choices" :key="choice.value" :value="String(choice.value)">
                  {{choice.value}} — {{Object.values(choice.labels)[0]??''}}
                </option>
              </select>
              <div v-else-if="['Lookup','Customer','Owner'].includes(field.type)" class="text-sm">
                <label v-for="relation in relations.filter(r=>r.referencingAttribute===field.logicalName)"
                  :key="relation.referencingNavigation" class="block">
                  {{relation.referencingNavigation}} → {{relation.referencedEntity}}
                  <input v-model.trim="relatedIds[relation.referencingNavigation!]" class="mt-1 w-full rounded border p-2" placeholder="Related record GUID">
                </label>
              </div>
              <input v-else v-model="draft[field.logicalName]"
                :type="['Integer','BigInt','Money','Decimal','Double'].includes(field.type)?'number':'text'"
                :step="['Money','Decimal','Double'].includes(field.type)?'any':undefined"
                class="w-full rounded border p-2">
            </template>
          </div>
        </fieldset>
        <section v-if="mode==='delete'" class="space-y-3 rounded-xl border border-red-300 bg-white p-4">
          <h3 class="font-semibold">{{t('uiDeleteWarning')}}</h3>
          <label class="block text-sm">{{t('uiDeleteReason')}}
            <textarea v-model="reason" rows="3" class="mt-1 w-full rounded border p-2" minlength="10"></textarea>
          </label>
          <label class="block text-sm">{{t('uiDeletePhraseInstruction')}}
            <input v-model="confirmationText" class="mt-1 w-full rounded border p-2" autocomplete="off">
          </label>
          <label class="flex items-center gap-2 text-sm">
            <input v-model="confirmation" type="checkbox">{{t('uiDeleteConfirm')}}
          </label>
        </section>
        <p v-if="api.error.value" class="text-sm text-red-700" role="alert">{{api.error.value}}</p>
        <p v-if="api.message.value" class="text-sm" role="status">{{api.message.value}}</p>
        <p v-if="result" class="rounded bg-slate-100 p-3 text-sm">Operation {{result.operationId}} · {{result.state}}</p>
        <button :disabled="api.busy.value || ambiguous" class="rounded bg-slate-900 px-4 py-2 text-white disabled:opacity-50"
          @click="save">{{t('uiSubmitWrite')}}</button>
        <p class="text-xs text-slate-600">{{t('uiWriteRules')}}</p>
      </template>
    </template>
  </section>
</template>
