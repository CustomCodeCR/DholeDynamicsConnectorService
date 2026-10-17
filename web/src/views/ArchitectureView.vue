<script setup lang="ts">
import { useI18n } from 'vue-i18n'
const { t } = useI18n()
</script>

<template>
  <section class="max-w-3xl rounded-xl border border-slate-200 bg-white p-7 shadow-sm">
    <h2 class="text-2xl font-semibold">{{ t('about') }}</h2>
    <p class="mt-4 font-medium text-slate-800">{{ t('phase') }}</p>
    <p class="mt-3 text-slate-600">{{ t('independent') }}</p>
    <p class="mt-4 text-slate-600">{{ t('next') }}</p>
  </section>
</template>
