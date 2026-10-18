<script setup lang="ts">
import { useI18n } from 'vue-i18n'

const { t, locale } = useI18n()
function changeLanguage(event: Event) {
  const element = event.target as HTMLSelectElement
  locale.value = element.value
}
</script>

<template>
  <div class="min-h-screen">
    <a href="#main" class="sr-only focus:not-sr-only focus:absolute focus:top-2 focus:left-2">
      {{ t('dashboard') }}
    </a>
    <header class="bg-slate-900 text-white shadow">
      <div class="mx-auto flex max-w-7xl flex-col gap-4 px-4 py-4 lg:flex-row lg:items-center lg:justify-between">
        <div>
          <h1 class="text-xl font-semibold tracking-tight">{{ t('app') }}</h1>
          <p class="mt-1 text-sm text-slate-300">{{ t('subtitle') }}</p>
        </div>
        <div class="flex flex-col gap-3 sm:flex-row sm:items-center">
          <nav class="flex flex-wrap gap-1 text-sm" aria-label="Main navigation">
            <RouterLink to="/" class="rounded px-2 py-2 hover:bg-slate-700">{{ t('dashboard') }}</RouterLink>
            <RouterLink to="/architecture" class="rounded px-2 py-2 hover:bg-slate-700">{{ t('architecture') }}</RouterLink>
            <RouterLink to="/admin" class="rounded px-2 py-2 hover:bg-slate-700">{{ t('admin') }}</RouterLink>
            <RouterLink to="/permissions" class="rounded px-2 py-2 hover:bg-slate-700">{{ t('uiPermissionsTitle') }}</RouterLink>
            <RouterLink to="/metadata" class="rounded px-2 py-2 hover:bg-slate-700">{{ t('metadataTitle') }}</RouterLink>
            <RouterLink to="/saved-queries" class="rounded px-2 py-2 hover:bg-slate-700">{{ t('savedQueries') }}</RouterLink>
            <RouterLink to="/legacy-migration" class="rounded px-2 py-2 hover:bg-slate-700">{{ t('legacyTitle') }}</RouterLink>
            <RouterLink to="/templates" class="rounded px-2 py-2 hover:bg-slate-700">{{ t('templateTitle') }}</RouterLink>
            <RouterLink to="/search" class="rounded px-2 py-2 hover:bg-slate-700">{{ t('uiSearchTitle') }}</RouterLink>
            <RouterLink to="/records" class="rounded px-2 py-2 hover:bg-slate-700">{{ t('uiEditorTitle') }}</RouterLink>
            <RouterLink to="/jobs" class="rounded px-2 py-2 hover:bg-slate-700">{{ t('uiJobsTitle') }}</RouterLink>
            <RouterLink to="/audit" class="rounded px-2 py-2 hover:bg-slate-700">{{ t('uiAuditTitle') }}</RouterLink>
            <RouterLink to="/settings" class="rounded px-2 py-2 hover:bg-slate-700">{{ t('uiSettingsTitle') }}</RouterLink>
          </nav>
          <label class="sr-only" for="language">Language</label>
          <select id="language" :value="locale" class="rounded border border-slate-500 bg-slate-800 p-2 text-sm" @change="changeLanguage">
            <option value="es">ES</option>
            <option value="en">EN</option>
          </select>
        </div>
      </div>
    </header>
    <main id="main" class="mx-auto max-w-7xl px-4 py-6 sm:py-9">
      <RouterView />
    </main>
  </div>
</template>
