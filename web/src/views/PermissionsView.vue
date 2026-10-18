<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAdminApi, type Connection, type Entity, type Policies } from '../lib/adminApi'
type Client = {id:string;issuer:string;clientId:string;displayName:string;enabled:boolean}
type Grant = {id:string;clientId:string;connectionId:string;entitySet:string;operation:string;
  allowedFields:string[];maxRequestsPerMinute:number;enabled:boolean}
const { t }=useI18n()
const api=useAdminApi()
const authenticated=ref(false)
const connections=ref<Connection[]>([])
const clients=ref<Client[]>([])
const grants=ref<Grant[]>([])
const code=ref('')
const tables=ref<Entity[]>([])
const policies=ref<Policies>({entities:[],fields:[]})
const entityName=ref('')
const operation=ref('read')
const clientId=ref('')
const checked=ref<string[]>([])
const quota=ref(30)
const issuer=ref('')
const application=ref('')
const displayName=ref('')
const chosen=computed(()=>tables.value.find(e=>e.logicalName===entityName.value))
const fields=computed(()=>{
  if(!chosen.value)return []
  if(operation.value==='delete')return chosen.value.columns.filter(f=>f.logicalName===chosen.value!.primaryIdAttribute)
  if(operation.value==='action')return []
  return api.allowedFields(chosen.value,policies.value,operation.value==='read'?'read':
    operation.value==='create'?'create':'update')
})
function resetFields(){checked.value=[]}
async function reload(){
  const [allClients,allGrants]=await Promise.all([
    api.request<Client[]>('/api/admin/v1/clients'),
    api.request<Grant[]>('/api/admin/v1/grants')
  ])
  clients.value=allClients;grants.value=allGrants
}
async function changeConnection(){
  await api.run(async()=>{
    const result=await api.loadCatalog(code.value)
    tables.value=result.entities;policies.value=result.policies
    entityName.value=tables.value[0]?.logicalName??''
    resetFields()
  })
}
async function register(){
  await api.run(async()=>{
    const registered=await api.request<Client>('/api/admin/v1/clients','POST',{
      issuer:issuer.value.trim(),clientId:application.value.trim(),
      displayName:displayName.value.trim(),enabled:true
    })
    clientId.value=registered.id
    await reload()
    api.message.value=t('uiClientRegistered')
  })
}
async function saveGrant(){
  await api.run(async()=>{
    if(!clientId.value||!entityName.value||checked.value.length<1)throw new Error(t('uiChooseColumns'))
    await api.request('/api/admin/v1/grants','POST',{
      clientId:clientId.value,connectionCode:code.value,
      entitySet:entityName.value,operation:operation.value,
      allowedFields:checked.value,maxRequestsPerMinute:quota.value,enabled:true
    })
    await reload()
    api.message.value=t('saved')
  })
}
onMounted(async()=>{
  authenticated.value=await api.loadSession()
  if(authenticated.value && api.session.value?.securityAdmin) {
    connections.value=await api.request<Connection[]>('/api/admin/v1/connections')
    await reload()
  }
})
</script>
<template>
  <section class="space-y-5">
    <h2 class="text-2xl font-semibold">{{t('uiPermissionsTitle')}}</h2>
    <p class="text-sm text-slate-600">{{t('grantNote')}}</p>
    <p v-if="!authenticated">{{t('signInRequired')}} <a class="underline" href="/auth/login">{{t('login')}}</a></p>
    <p v-else-if="!api.session.value?.securityAdmin" role="alert">{{t('uiSecurityAdminOnly')}}</p>
    <template v-else>
      <p v-if="api.error.value" role="alert" class="text-red-700">{{api.error.value}}</p>
      <p v-if="api.message.value" role="status">{{api.message.value}}</p>
      <section class="space-y-3 rounded-xl border bg-white p-5">
        <h3 class="font-semibold">{{t('registerM2M')}}</h3>
        <div class="grid gap-3 sm:grid-cols-3">
          <label class="text-sm">Issuer HTTPS<input v-model="issuer" type="url" required class="mt-1 block w-full rounded border p-2"></label>
          <label class="text-sm">OAuth Client ID<input v-model="application" required class="mt-1 block w-full rounded border p-2"></label>
          <label class="text-sm">{{t('displayName')}}<input v-model="displayName" class="mt-1 block w-full rounded border p-2"></label>
        </div>
        <button :disabled="api.busy.value" class="rounded border px-4 py-2" @click="register">{{t('registered')}}</button>
      </section>
      <section class="space-y-3 rounded-xl border bg-white p-5">
        <h3 class="font-semibold">{{t('grantPermissions')}}</h3>
        <div class="grid gap-3 sm:grid-cols-2">
          <label class="text-sm">{{t('selectConnection')}}
            <select v-model="code" class="mt-1 block w-full rounded border p-2" @change="changeConnection">
              <option value="">{{t('selectConnection')}}</option>
              <option v-for="c in connections" :key="c.id" :value="c.code">{{c.code}}</option>
            </select>
          </label>
          <label class="text-sm">{{t('selectClient')}}
            <select v-model="clientId" class="mt-1 block w-full rounded border p-2">
              <option value="">{{t('selectClient')}}</option>
              <option v-for="c in clients" :key="c.id" :value="c.id">{{c.displayName}} ({{c.clientId}})</option>
            </select>
          </label>
          <label class="text-sm">{{t('templateEntity')}}
            <select v-model="entityName" class="mt-1 block w-full rounded border p-2" @change="resetFields">
              <option v-for="e in tables" :key="e.logicalName" :value="e.logicalName">{{e.logicalName}}</option>
            </select>
          </label>
          <label class="text-sm">{{t('operation')}}
            <select v-model="operation" class="mt-1 block w-full rounded border p-2" @change="resetFields">
              <option v-for="op in ['read','create','update','upsert','delete']" :key="op" :value="op">{{op}}</option>
            </select>
          </label>
        </div>
        <fieldset class="rounded border p-3">
          <legend class="font-medium text-sm">{{t('uiColumns')}}</legend>
          <label v-for="f in fields" :key="f.logicalName" class="mr-4 inline-flex items-center gap-2 py-2 text-sm">
            <input v-model="checked" type="checkbox" :value="f.logicalName">{{f.logicalName}}
          </label>
        </fieldset>
        <label class="block text-sm">{{t('quota')}}
          <input v-model.number="quota" type="number" min="1" max="600" class="ml-2 rounded border p-2">
        </label>
        <button :disabled="api.busy.value || !clientId || !code || !checked.length" class="rounded bg-slate-900 px-4 py-2 text-white" @click="saveGrant">{{t('save')}}</button>
      </section>
      <section class="rounded-xl border bg-white p-5">
        <h3 class="font-semibold">{{t('uiEffectiveGrants')}}</h3>
        <div class="overflow-x-auto">
          <table class="w-full text-left text-sm">
            <thead><tr><th class="p-2">{{t('selectClient')}}</th><th class="p-2">{{t('selectConnection')}}</th>
              <th class="p-2">{{t('templateEntity')}}</th><th class="p-2">{{t('operation')}}</th>
              <th class="p-2">{{t('uiColumns')}}</th></tr></thead>
            <tbody><tr v-for="grant in grants.filter(g=>!code || g.connectionId===connections.find(c=>c.code===code)?.id)" :key="grant.id" class="border-t">
              <td class="p-2">{{clients.find(c=>c.id===grant.clientId)?.displayName??grant.clientId}}</td>
              <td class="p-2">{{connections.find(c=>c.id===grant.connectionId)?.code??'—'}}</td>
              <td class="p-2">{{grant.entitySet}}</td><td class="p-2">{{grant.operation}}</td>
              <td class="p-2">{{grant.allowedFields.join(', ')}}</td>
            </tr></tbody>
          </table>
        </div>
      </section>
    </template>
  </section>
</template>
