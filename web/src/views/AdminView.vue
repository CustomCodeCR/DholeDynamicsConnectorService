<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'

type Session = { authenticated: boolean; actor: string; securityAdmin: boolean; csrfToken: string }
type Connection = { id: string; code: string; dataverseUrl: string; tenantId: string; clientId: string; defaultCurrencyId?:string|null }
type OAuthClient = { id: string; issuer: string; clientId: string; displayName: string; enabled: boolean }
type Grant = { id: string; clientId: string; connectionId: string; entitySet: string; operation: string; allowedFields: string[]; maxRequestsPerMinute: number; enabled: boolean }

const { t } = useI18n()
const session = ref<Session | null>(null)
const status = ref<'loading' | 'unauthenticated' | 'ready'>('loading')
const busy = ref(false)
const notice = ref('')
const connections = ref<Connection[]>([])
const clients = ref<OAuthClient[]>([])
const grants = ref<Grant[]>([])
const connection = ref({ code: '', dataverseUrl: '', tenantId: '', clientId: '', clientSecret: '', defaultCurrencyId: '' })
const rotate = ref({ code: '', clientSecret: '' })
const client = ref({ issuer: '', clientId: '', displayName: '', enabled: true })
const grant = ref({ clientId: '', connectionCode: '', entitySet: 'quotes',
  operation: 'read', allowedFields: 'quoteid', maxRequestsPerMinute: 30, enabled: true })

async function api(path: string, method = 'GET', data?: unknown) {
  const response = await fetch(path, {
    method, credentials: 'same-origin', cache: 'no-store',
    headers: {
      ...(data ? { 'Content-Type': 'application/json' } : {}),
      ...(method !== 'GET' && session.value ? { 'X-CSRF-TOKEN': session.value.csrfToken } : {}),
    },
    ...(data ? { body: JSON.stringify(data) } : {}),
  })
  if (!response.ok) throw new Error(`HTTP ${response.status}`)
  if (response.status === 204) return null
  return response.json()
}
async function reload() {
  status.value = 'loading'
  try {
    const response = await fetch('/auth/session', { credentials: 'same-origin', cache: 'no-store' })
    if (!response.ok) { status.value = 'unauthenticated'; return }
    session.value = await response.json() as Session
    connections.value = await api('/api/admin/v1/connections') as Connection[]
    if (session.value?.securityAdmin) {
      clients.value = await api('/api/admin/v1/clients') as OAuthClient[]
      grants.value = await api('/api/admin/v1/grants') as Grant[]
    }
    status.value = 'ready'
  } catch {
    status.value = 'unauthenticated'
  }
}
async function action(task: () => Promise<unknown>, result: string) {
  busy.value = true
  notice.value = ''
  try {
    await task()
    if (!notice.value) notice.value = result
    if (session.value) {
      connections.value = await api('/api/admin/v1/connections') as Connection[]
      if (session.value.securityAdmin) {
        clients.value = await api('/api/admin/v1/clients') as OAuthClient[]
        grants.value = await api('/api/admin/v1/grants') as Grant[]
      }
    }
  } catch (error) {
    notice.value = error instanceof Error ? error.message : t('error')
  } finally { busy.value = false }
}
function editConnection(item: Connection) {
  connection.value = {
    code: item.code, dataverseUrl: item.dataverseUrl,
    tenantId: item.tenantId, clientId: item.clientId,
    clientSecret: '', defaultCurrencyId: item.defaultCurrencyId ?? ''
  }
  document.getElementById('connection-form')?.scrollIntoView({ behavior: 'smooth' })
}
async function saveConnection() {
  const { code, dataverseUrl, tenantId, clientId, clientSecret, defaultCurrencyId } = connection.value
  await action(() => api('/api/admin/v1/connections', 'POST', {
    code, dataverseUrl, tenantId, clientId, clientSecret,
    defaultCurrencyId: defaultCurrencyId || null,
  }), t('saved'))
  connection.value.clientSecret = ''
}
async function rotateSecret() {
  await action(() => api(`/api/admin/v1/connections/${encodeURIComponent(rotate.value.code)}/secret`,
    'PUT', { clientSecret: rotate.value.clientSecret }), t('saved'))
  rotate.value.clientSecret = ''
}
async function check(code: string) {
  await action(async () => {
    const result = await api(`/api/admin/v1/connections/${encodeURIComponent(code)}/test`, 'POST')
    notice.value = `WhoAmI: ${result.applicationUserId} · quotes.read: ${result.canReadQuotes}`
  }, t('connected'))
}
async function registerClient() {
  const record = await api('/api/admin/v1/clients', 'POST', client.value) as OAuthClient
  grant.value.clientId = record.id
  notice.value = `${t('registered')}: ${record.id}`
}
async function saveGrant() {
  const fields = grant.value.allowedFields.split(',').map(x => x.trim()).filter(Boolean)
  await api('/api/admin/v1/grants', 'POST', { ...grant.value, allowedFields: fields })
  notice.value = t('saved')
}
async function logout() {
  await action(async () => {
    await api('/auth/logout', 'POST')
    session.value = null
    status.value = 'unauthenticated'
  }, t('loggedOut'))
}
onMounted(() => { void reload() })
</script>

<template>
  <section class="space-y-6">
    <h2 class="text-2xl font-semibold">{{ t('admin') }}</h2>
    <p v-if="status === 'loading'" role="status">{{ t('loading') }}</p>
    <section v-else-if="status === 'unauthenticated'" class="rounded-xl border bg-white p-6">
      <p class="mb-4 text-slate-700">{{ t('signInRequired') }}</p>
      <a href="/auth/login" class="inline-block rounded-lg bg-slate-900 px-4 py-2 text-white">{{ t('login') }}</a>
    </section>
    <template v-else>
      <div class="flex items-center justify-between rounded-xl bg-white p-4 shadow-sm">
        <p class="text-sm">{{ t('signedIn') }}: {{ session?.actor }}</p>
        <button class="rounded border px-3 py-2" @click="logout">{{ t('logout') }}</button>
      </div>
      <p v-if="notice" role="status" aria-live="polite" class="rounded bg-slate-100 px-4 py-3 text-sm">{{ notice }}</p>
      <section class="rounded-xl border bg-white p-6">
        <h3 class="text-lg font-semibold">{{ t('connections') }}</h3>
        <div v-if="connections.length === 0" class="mt-3 text-sm text-slate-600">{{ t('noConnections') }}</div>
        <ul class="mt-3 space-y-2">
          <li v-for="item in connections" :key="item.id" class="flex flex-wrap items-center justify-between gap-3 rounded border p-3">
            <div><strong>{{ item.code }}</strong><p class="text-xs text-slate-600">{{ item.dataverseUrl }}</p></div>
            <div class="flex flex-wrap gap-2"><button :disabled="busy" class="rounded border px-3 py-2 text-sm" @click="editConnection(item)">{{ t('uiEditConnection') }}</button>
            <button :disabled="busy" class="rounded border px-3 py-2 text-sm" @click="check(item.code)">{{ t('testConnection') }}</button></div>
          </li>
        </ul>
      </section>
      <form id="connection-form" class="grid gap-3 rounded-xl border bg-white p-6 sm:grid-cols-2" @submit.prevent="saveConnection">
        <h3 class="text-lg font-semibold sm:col-span-2">{{ t('configureConnection') }}</h3><p class="text-sm text-slate-600 sm:col-span-2">{{t('uiNewSecretOnEdit')}}</p>
        <label class="text-sm">{{ t('code') }}<input v-model="connection.code" required maxlength="64" class="mt-1 w-full rounded border p-2" autocomplete="off" /></label>
        <label class="text-sm">Dataverse HTTPS URL<input v-model="connection.dataverseUrl" required type="url" class="mt-1 w-full rounded border p-2" placeholder="https://org.crm.dynamics.com" /></label>
        <label class="text-sm">Tenant ID<input v-model="connection.tenantId" required class="mt-1 w-full rounded border p-2" /></label>
        <label class="text-sm">Application (client) ID<input v-model="connection.clientId" required class="mt-1 w-full rounded border p-2" /></label>
        <label class="text-sm">Client secret<input v-model="connection.clientSecret" required type="password" class="mt-1 w-full rounded border p-2" autocomplete="new-password" /></label>
        <label class="text-sm">Currency GUID (optional)<input v-model="connection.defaultCurrencyId" class="mt-1 w-full rounded border p-2" /></label>
        <button :disabled="busy" type="submit" class="rounded bg-slate-900 px-4 py-2 text-white sm:col-span-2">{{ t('save') }}</button>
      </form>
      <form class="grid gap-3 rounded-xl border bg-white p-6 sm:grid-cols-2" @submit.prevent="rotateSecret">
        <h3 class="text-lg font-semibold sm:col-span-2">{{ t('rotateSecret') }}</h3>
        <label class="text-sm">{{ t('code') }}<input v-model="rotate.code" required class="mt-1 w-full rounded border p-2" /></label>
        <label class="text-sm">New secret<input v-model="rotate.clientSecret" required type="password" class="mt-1 w-full rounded border p-2" autocomplete="new-password" /></label>
        <button :disabled="busy" class="rounded bg-slate-900 px-4 py-2 text-white sm:col-span-2">{{ t('rotateSecret') }}</button>
      </form>
      <template v-if="session?.securityAdmin">
        <section class="rounded-xl border bg-white p-6">
          <h3 class="text-lg font-semibold">{{ t('registerM2M') }}</h3>
          <ul class="mt-3 space-y-2 text-sm">
            <li v-for="item in clients" :key="item.id" class="rounded border p-3">
              <strong>{{ item.displayName }}</strong> — {{ item.enabled ? t('enabled') : t('disabled') }}
              <p class="break-all text-xs text-slate-600">{{ item.id }}</p>
              <p class="break-all text-xs text-slate-600">{{ item.clientId }}</p>
            </li>
          </ul>
        </section>
        <section class="rounded-xl border bg-white p-6">
          <h3 class="text-lg font-semibold">{{ t('grantPermissions') }}</h3>
          <ul class="mt-3 space-y-2 text-sm">
            <li v-for="item in grants" :key="item.id" class="rounded border p-3">
              <strong>{{ item.entitySet }} — {{ item.operation }}</strong>
              <p>{{ item.allowedFields.join(', ') }}</p>
              <p class="break-all text-xs text-slate-600">{{ item.clientId }} · {{ item.connectionId }} · {{ item.maxRequestsPerMinute }}/min</p>
            </li>
          </ul>
        </section>
        <form class="grid gap-3 rounded-xl border bg-white p-6 sm:grid-cols-2" @submit.prevent="action(registerClient, t('registered'))">
          <h3 class="text-lg font-semibold sm:col-span-2">{{ t('registerM2M') }}</h3>
          <label class="text-sm">OAuth issuer<input v-model="client.issuer" required class="mt-1 w-full rounded border p-2" placeholder="https://login.microsoftonline.com/{tenant}/v2.0" /></label>
          <label class="text-sm">OAuth client ID<input v-model="client.clientId" required class="mt-1 w-full rounded border p-2" /></label>
          <label class="text-sm">{{ t('displayName') }}<input v-model="client.displayName" required class="mt-1 w-full rounded border p-2" /></label>
          <label class="flex items-center gap-2 text-sm"><input v-model="client.enabled" type="checkbox" />{{ t('enabled') }}</label>
          <button :disabled="busy" class="rounded bg-slate-900 px-4 py-2 text-white sm:col-span-2">{{ t('save') }}</button>
        </form>
        <form class="grid gap-3 rounded-xl border bg-white p-6 sm:grid-cols-2" @submit.prevent="action(saveGrant, t('saved'))">
          <h3 class="text-lg font-semibold sm:col-span-2">{{ t('grantPermissions') }}</h3>
          <label class="text-sm">Registered client
            <select v-model="grant.clientId" required class="mt-1 w-full rounded border p-2">
              <option value="" disabled>{{ t('selectClient') }}</option>
              <option v-for="item in clients" :key="item.id" :value="item.id">{{ item.displayName }} ({{ item.clientId }})</option>
            </select>
          </label>
          <label class="text-sm">{{ t('code') }}
            <select v-model="grant.connectionCode" required class="mt-1 w-full rounded border p-2">
              <option value="" disabled>{{ t('selectConnection') }}</option>
              <option v-for="item in connections" :key="item.id" :value="item.code">{{ item.code }}</option>
            </select>
          </label>
          <label class="text-sm">Entity set<input v-model="grant.entitySet" readonly class="mt-1 w-full rounded border bg-slate-100 p-2" /></label>
          <label class="text-sm">{{ t('operation') }}<select v-model="grant.operation" class="mt-1 w-full rounded border p-2"><option value="read">read</option><option value="create">create</option></select></label>
          <label class="text-sm sm:col-span-2">{{ t('fields') }}<textarea v-model="grant.allowedFields" required class="mt-1 w-full rounded border p-2" rows="3"></textarea></label>
          <label class="text-sm">{{ t('quota') }}<input v-model.number="grant.maxRequestsPerMinute" required type="number" min="1" max="600" class="mt-1 w-full rounded border p-2" /></label>
          <label class="flex items-center gap-2 text-sm"><input v-model="grant.enabled" type="checkbox" />{{ t('enabled') }}</label>
          <p class="text-xs text-slate-600 sm:col-span-2">{{ t('grantNote') }}</p>
          <button :disabled="busy" class="rounded bg-slate-900 px-4 py-2 text-white sm:col-span-2">{{ t('save') }}</button>
        </form>
      </template>
    </template>
  </section>
</template>
