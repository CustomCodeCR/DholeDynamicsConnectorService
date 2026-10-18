<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useHealthStore, type HealthState } from '../stores/health'
import { useAdminApi } from '../lib/adminApi'
type Overview = { connections:number;clients:number;activeTemplates:number;needsReconciliation:number;
  rejectedWrites24h:number;requestsLastHour:number;lastEventAt:string|null;requestP95Ms:number;httpErrors24h:number;dynamicsQuotaNote:string }
type Job = {id:string;entity:string;operation:string;state:string;startedAt:string}
const { t }=useI18n()
const health=useHealthStore()
const api=useAdminApi()
const authenticated=ref(false)
const overview=ref<Overview|null>(null)
const jobs=ref<Job[]>([])
const label=(state:HealthState)=>t(state==='healthy'?'healthy':state==='unknown'?'pending':'unavailable')
async function refresh(){
  await health.refresh()
  authenticated.value=await api.loadSession()
  if(!authenticated.value){overview.value=null;jobs.value=[];return}
  await api.run(async()=>{
    const [metrics,recent]=await Promise.all([
      api.request<Overview>('/api/admin/v1/console/overview'),
      api.request<Job[]>('/api/admin/v1/console/jobs?limit=6')
    ])
    overview.value=metrics;jobs.value=recent
  })
}
onMounted(()=>{void refresh()})
</script>
<template>
  <section class="space-y-6">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <div>
        <h2 class="text-2xl font-semibold">{{t('dashboard')}}</h2>
        <p class="text-sm text-slate-600">{{t('uiDashboardIntro')}}</p>
      </div>
      <button :disabled="health.busy||api.busy.value" class="rounded bg-slate-900 px-4 py-2 text-white" @click="refresh">{{t('refresh')}}</button>
    </div>
    <div class="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
      <article v-for="item in [
        {name:t('live'),value:label(health.live)},
        {name:t('ready'),value:label(health.ready)},
        {name:t('connections'),value:overview?.connections??'—'},
        {name:t('uiPendingReview'),value:overview?.needsReconciliation??'—'}
      ]" :key="item.name" class="rounded-xl border bg-white p-5 shadow-sm">
        <h3 class="text-sm text-slate-600">{{item.name}}</h3>
        <p class="mt-3 text-2xl font-semibold">{{item.value}}</p>
      </article>
    </div>
    <p v-if="health.failed" role="alert" class="text-sm text-red-700">{{t('error')}}</p>
    <section v-if="authenticated && overview" class="grid gap-4 sm:grid-cols-3">
      <article v-for="item in [
        {label:t('uiOAuthClients'),value:overview.clients},
        {label:t('uiActiveTemplates'),value:overview.activeTemplates},
        {label:t('uiLocalRequests'),value:overview.requestsLastHour},
        {label:t('uiRejected24h'),value:overview.rejectedWrites24h},
        {label:t('uiP95Latency'),value:Math.round(overview.requestP95Ms)+' ms'},
        {label:t('uiHttpErrors'),value:overview.httpErrors24h}
      ]" :key="item.label" class="rounded-xl border bg-white p-5">
        <h3 class="text-sm text-slate-600">{{item.label}}</h3>
        <p class="mt-2 text-2xl font-semibold">{{item.value}}</p>
      </article>
      <div class="rounded-xl border bg-white p-5 sm:col-span-2">
        <h3 class="font-semibold">{{t('uiQuotaNotice')}}</h3>
        <p class="mt-2 text-sm text-slate-600">{{t('uiQuotaExplanation')}}</p>
        <p class="mt-2 text-xs">{{t('uiLastEvent')}}: {{overview.lastEventAt?new Date(overview.lastEventAt).toLocaleString():'—'}}</p>
      </div>
    </section>
    <section v-if="authenticated" class="space-y-3 rounded-xl border bg-white p-5">
      <div class="flex flex-wrap justify-between gap-3">
        <h3 class="font-semibold">{{t('uiRecentOperations')}}</h3>
        <RouterLink class="underline text-sm" to="/jobs">{{t('uiJobsTitle')}}</RouterLink>
      </div>
      <p v-if="!jobs.length" class="text-sm text-slate-600">{{t('uiNoJobs')}}</p>
      <div v-for="job in jobs" :key="job.id" class="flex flex-wrap justify-between gap-2 border-t py-2 text-sm">
        <span>{{job.entity}} · {{job.operation}} · {{job.state}}</span>
        <span>{{new Date(job.startedAt).toLocaleString()}}</span>
      </div>
    </section>
    <p v-else class="text-sm">{{t('signInRequired')}} <a href="/auth/login" class="underline">{{t('login')}}</a></p>
  </section>
</template>
