import { createRouter, createWebHistory } from 'vue-router'
import DashboardView from './views/DashboardView.vue'
import ArchitectureView from './views/ArchitectureView.vue'
import AdminView from './views/AdminView.vue'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: DashboardView, name: 'dashboard' },
    { path: '/architecture', component: ArchitectureView, name: 'architecture' },
    { path: '/admin', component: AdminView, name: 'admin' },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})
