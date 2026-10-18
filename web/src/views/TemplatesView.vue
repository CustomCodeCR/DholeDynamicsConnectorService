<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'

type Entity = { logicalName: string; columns: Array<{logicalName:string;type:string}> }
type Template = { id:string;name:string;activeVersion:number|null;history:Array<{
  version:number;metadataVersion:number;createdAt:string;definition:Record<string,unknown> }> }
type Policies = {entities:Array<{logicalName:string;enabled:boolean}>;fields:Array<{
  entityName:string;fieldName:string;enabled:boolean;canCreate:boolean;canUpdate:boolean }> }
const { t } = useI18n()
const authorized=ref(false)
const csrf=ref('')
const connections=ref<Array<{code:string}>>([])
const code=ref('')
const entities=ref<Entity[]>([])
const policies=ref<Policies>({entities:[],fields:[]})
const templates=ref<Template[]>([])
const templateName=ref('')
const entityName=ref('')
const fieldName=ref('')
const operation=ref('create')
const sourceName=ref('value')
const transform=ref('trim')
const definition=ref('')
const inputs=ref('{"value":"Sample"}')
const output=ref('')
const importText=ref('')
const busy=ref(false)
const message=ref('')
const selectedRevision=ref(1)
const activeTemplate=ref('')
const actionName=ref('')
const actionParam=ref('')
const actions=ref<Array<{entityName:string;actionName:string;enabled:boolean}>>([])

function base() { return '/api/admin/v1/connections/'+encodeURIComponent(code.value) }
function templatesUrl(name?:string) {
  return base()+'/templates'+(name?'/'+encodeURIComponent(name):'')
}
async function request<T>(url:string,method='GET',body?:unknown):Promise<T> {
  const r=await fetch(url,{method,credentials:'same-origin',cache:'no-store',
    headers:{...(body!==undefined?{'Content-Type':'application/json'}:{}),
      ...(method!=='GET'?{'X-CSRF-TOKEN':csrf.value}:{})},
    ...(body!==undefined?{body:JSON.stringify(body)}:{})})
  if(!r.ok) throw new Error('HTTP '+r.status)
  return r.status===204?null as T:await r.json() as T
}
async function run(task:()=>Promise<void>) {
  busy.value=true;message.value=''
  try {await task()}catch(e){message.value=e instanceof Error?e.message:t('error')}
  finally{busy.value=false}
}
function allowedEntities() {
  return entities.value.filter(e=>policies.value.entities.some(
    p=>p.logicalName===e.logicalName&&p.enabled))
}
function allowedFields() {
  return entities.value.find(e=>e.logicalName===entityName.value)?.columns.filter(c=>
    policies.value.fields.some(p=>p.entityName===entityName.value &&
      p.fieldName===c.logicalName&&p.enabled &&
      (operation.value==='create'?p.canCreate:p.canUpdate)))??[]
}
function makeDefinition() {
  if(!entityName.value)return
  const writable=['create','update','upsert'].includes(operation.value)
  const data={
    entityName:entityName.value,
    operation:operation.value,
    inputs:sourceName.value?[{name:sourceName.value,type:'string',required:true}]:[],
    mappings:writable&&fieldName.value?[{
      target:fieldName.value,source:sourceName.value,
      steps:transform.value==='none'?[]:[{op:transform.value}]
    }]:[],
    ...(operation.value==='read'?{select:[fieldName.value]}:{}),
    ...(operation.value==='find'?{savedQuery:activeTemplate.value}:{}),
    ...(operation.value==='action'?{actionName:actionName.value,
      mappings:actionParam.value?[{target:actionParam.value,source:sourceName.value}]:[]}:{})
  }
  definition.value=JSON.stringify(data,null,2)
}
async function loadConnection(){
  if(!code.value)return
  const [metadata,grants,available]=await Promise.all([
    request<{catalog:{tables:Entity[]}}>(base()+'/metadata'),
    request<Policies>(base()+'/metadata/policies'),
    request<Template[]>(base()+'/templates')
  ])
  entities.value=metadata.catalog.tables
  policies.value=grants
  templates.value=available
  entityName.value=allowedEntities()[0]?.logicalName??''
  fieldName.value=allowedFields()[0]?.logicalName??''
  makeDefinition()
  try{actions.value=await request<typeof actions.value>(base()+'/allowed-actions')}
  catch{actions.value=[]}
}
async function refreshTemplates(){templates.value=await request<Template[]>(base()+'/templates')}
async function create(){
  await run(async()=>{
    if(!templateName.value)throw new Error(t('templateNameRequired'))
    await request(templatesUrl(templateName.value)+'/versions','POST',JSON.parse(definition.value))
    await refreshTemplates();message.value=t('templateCreated')
  })
}
async function activate(item:Template,version:number|null){
  await run(async()=>{
    await request(templatesUrl(item.name)+'/active','PUT',{version})
    await refreshTemplates();message.value=t('saved')
  })
}
async function preview(item:Template,version:number){
  await run(async()=>{
    const result=await request<unknown>(templatesUrl(item.name)+'/versions/'+version+'/preview',
      'POST',JSON.parse(inputs.value))
    output.value=JSON.stringify(result,null,2)
    message.value=t('templatePreviewOnly')
  })
}
async function exportVersion(item:Template,version:number){
  await run(async()=>{
    const data=await request<unknown>(templatesUrl(item.name)+'/export?version='+version)
    importText.value=JSON.stringify(data,null,2)
    message.value=t('templateExported')
  })
}
async function importDefinition(){
  await run(async()=>{
    await request(base()+'/templates/import','POST',JSON.parse(importText.value))
    await refreshTemplates();message.value=t('templateImported')
  })
}
async function setActionPolicy() {
  await run(async()=>{
    await request(base()+'/allowed-actions','POST',{
      entityName:entityName.value,actionName:actionName.value,
      parameters:actionParam.value?[{name:actionParam.value,type:'string',required:true}]:[],
      enabled:false
    })
    actions.value=await request<typeof actions.value>(base()+'/allowed-actions')
    message.value=t('templateActionCreated')
  })
}
async function toggleAction(action:{entityName:string;actionName:string;enabled:boolean}) {
  await run(async()=>{
    await request(base()+'/allowed-actions/'+encodeURIComponent(action.entityName)+
      '/'+encodeURIComponent(action.actionName)+'/enabled','PUT',{enabled:!action.enabled})
    actions.value=await request<typeof actions.value>(base()+'/allowed-actions')
  })
}
onMounted(async()=>{
  try{
    const r=await fetch('/auth/session',{credentials:'same-origin',cache:'no-store'})
    if(!r.ok)return
    csrf.value=(await r.json() as {csrfToken:string}).csrfToken
    connections.value=await request<Array<{code:string}>>('/api/admin/v1/connections')
    authorized.value=true
  }catch{authorized.value=false}
})
</script>

<template>
  <section class="space-y-5">
    <h2 class="text-2xl font-semibold">{{t('templateTitle')}}</h2>
    <p class="text-sm text-slate-600">{{t('templateIntro')}}</p>
    <p v-if="!authorized">{{t('signInRequired')}} <a class="underline" href="/auth/login">{{t('login')}}</a></p>
    <template v-else>
      <label class="block text-sm">{{t('selectConnection')}}
        <select v-model="code" class="mt-1 block w-full rounded border p-2" @change="run(loadConnection)">
          <option value="">{{t('selectConnection')}}</option>
          <option v-for="c in connections" :key="c.code" :value="c.code">{{c.code}}</option>
        </select>
      </label>
      <p v-if="message" role="status" aria-live="polite">{{message}}</p>
      <template v-if="code">
        <section class="space-y-4 rounded-xl border bg-white p-4">
          <h3 class="font-semibold">{{t('templateCreate')}}</h3>
          <div class="grid gap-3 sm:grid-cols-2">
            <label class="text-sm">{{t('templateName')}}
              <input v-model="templateName" class="mt-1 block w-full rounded border p-2" placeholder="create_quote">
            </label>
            <label class="text-sm">{{t('templateOperation')}}
              <select v-model="operation" class="mt-1 block w-full rounded border p-2" @change="makeDefinition">
                <option v-for="op in ['create','update','upsert','read','find','delete','action']" :key="op" :value="op">{{op}}</option>
              </select>
            </label>
            <label class="text-sm">{{t('templateEntity')}}
              <select v-model="entityName" class="mt-1 block w-full rounded border p-2" @change="makeDefinition">
                <option v-for="e in allowedEntities()" :key="e.logicalName" :value="e.logicalName">{{e.logicalName}}</option>
              </select>
            </label>
            <label class="text-sm">{{t('templateTarget')}}
              <select v-model="fieldName" class="mt-1 block w-full rounded border p-2" @change="makeDefinition">
                <option v-for="f in allowedFields()" :key="f.logicalName" :value="f.logicalName">{{f.logicalName}}</option>
              </select>
            </label>
            <label class="text-sm">{{t('templateSource')}}
              <input v-model="sourceName" class="mt-1 block w-full rounded border p-2" @change="makeDefinition">
            </label>
            <label class="text-sm">{{t('templateTransform')}}
              <select v-model="transform" class="mt-1 block w-full rounded border p-2" @change="makeDefinition">
                <option v-for="op in ['none','trim','upper','lower','date_utc','decimal']" :key="op" :value="op">{{op}}</option>
              </select>
            </label>
          </div>
          <p class="text-xs text-slate-600">{{t('templateJsonInfo')}}</p>
          <textarea v-model="definition" rows="13" spellcheck="false" class="w-full rounded border p-3 font-mono text-xs"></textarea>
          <button :disabled="busy" class="rounded bg-slate-900 px-4 py-2 text-white" @click="create">{{t('templateCreateVersion')}}</button>
        </section>
        <section class="space-y-3 rounded-xl border bg-white p-4">
          <h3 class="font-semibold">{{t('templateHistory')}}</h3>
          <label class="block text-sm">{{t('templatePreviewInputs')}}
            <textarea v-model="inputs" rows="3" class="mt-1 w-full rounded border p-2 font-mono text-xs"></textarea>
          </label>
          <p v-if="templates.length===0" class="text-sm">{{t('templateNone')}}</p>
          <article v-for="item in templates" :key="item.id" class="rounded border p-3">
            <h4 class="font-semibold">{{item.name}} — {{t('templateActive')}} {{item.activeVersion??'—'}}</h4>
            <div v-for="revision in item.history" :key="revision.version" class="mt-2 flex flex-wrap items-center gap-2 border-t pt-2 text-xs">
              <strong>v{{revision.version}}</strong>
              <span>metadata v{{revision.metadataVersion}}</span>
              <button :disabled="busy" class="rounded border px-3 py-1" @click="preview(item,revision.version)">{{t('templatePreview')}}</button>
              <button :disabled="busy" class="rounded border px-3 py-1" @click="activate(item,revision.version)">{{t('templateActivate')}}</button>
              <button :disabled="busy" class="rounded border px-3 py-1" @click="exportVersion(item,revision.version)">{{t('templateExport')}}</button>
            </div>
            <button :disabled="busy" class="mt-2 rounded border px-3 py-1 text-xs" @click="activate(item,null)">{{t('templateDeactivate')}}</button>
          </article>
          <pre v-if="output" class="max-h-96 overflow-auto rounded bg-slate-100 p-3 text-xs">{{output}}</pre>
        </section>
        <section class="space-y-2 rounded-xl border bg-white p-4">
          <h3 class="font-semibold">{{t('templatePromotion')}}</h3>
          <p class="text-xs">{{t('templatePromotionNote')}}</p>
          <textarea v-model="importText" rows="7" class="w-full rounded border p-2 font-mono text-xs"></textarea>
          <button :disabled="busy" class="rounded border px-4 py-2" @click="importDefinition">{{t('templateImport')}}</button>
        </section>
        <section class="space-y-2 rounded-xl border bg-white p-4">
          <h3 class="font-semibold">{{t('templateActionPolicy')}}</h3>
          <p class="text-xs">{{t('templateActionNote')}}</p>
          <label class="block text-sm">{{t('templateAction')}}
            <input v-model="actionName" class="mt-1 block w-full rounded border p-2" placeholder="new_CloseShipment">
          </label>
          <label class="block text-sm">{{t('templateActionParam')}}
            <input v-model="actionParam" class="mt-1 block w-full rounded border p-2" placeholder="reason">
          </label>
          <button :disabled="busy" class="rounded border px-3 py-2" @click="setActionPolicy">{{t('save')}}</button>
          <div v-for="act in actions" :key="act.entityName+act.actionName" class="flex items-center justify-between border-t py-2 text-sm">
            <span>{{act.entityName}} / {{act.actionName}}</span>
            <button :disabled="busy" class="rounded border px-3 py-1" @click="toggleAction(act)">{{act.enabled?t('disable'):t('enable')}}</button>
          </div>
        </section>
      </template>
    </template>
  </section>
</template>
