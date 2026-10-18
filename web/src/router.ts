import { createRouter, createWebHistory } from 'vue-router'
import DashboardView from './views/DashboardView.vue'
import ArchitectureView from './views/ArchitectureView.vue'
import AdminView from './views/AdminView.vue'
import MetadataView from './views/MetadataView.vue'
import SavedQueriesView from './views/SavedQueriesView.vue'
import TemplatesView from './views/TemplatesView.vue'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: DashboardView, name: 'dashboard' },
    { path: '/architecture', component: ArchitectureView, name: 'architecture' },
    { path: '/admin', component: AdminView, name: 'admin' },
    { path: '/metadata', component: MetadataView, name: 'metadata' },
    { path: '/saved-queries', component: SavedQueriesView, name: 'saved-queries' },
    { path: '/templates', component: TemplatesView, name: 'templates' },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})
