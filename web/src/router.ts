import { createRouter, createWebHistory } from 'vue-router'
import DashboardView from './views/DashboardView.vue'
import ArchitectureView from './views/ArchitectureView.vue'
import AdminView from './views/AdminView.vue'
import MetadataView from './views/MetadataView.vue'
import SavedQueriesView from './views/SavedQueriesView.vue'
import TemplatesView from './views/TemplatesView.vue'
import SearchView from './views/SearchView.vue'
import RecordsView from './views/RecordsView.vue'
import JobsView from './views/JobsView.vue'
import AuditView from './views/AuditView.vue'
import SettingsView from './views/SettingsView.vue'
import PermissionsView from './views/PermissionsView.vue'
import LegacyMigrationView from './views/LegacyMigrationView.vue'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: DashboardView, name: 'dashboard' },
    { path: '/architecture', component: ArchitectureView, name: 'architecture' },
    { path: '/admin', component: AdminView, name: 'admin' },
    { path: '/metadata', component: MetadataView, name: 'metadata' },
    { path: '/saved-queries', component: SavedQueriesView, name: 'saved-queries' },
    { path: '/templates', component: TemplatesView, name: 'templates' },
    { path: '/legacy-migration', component: LegacyMigrationView, name: 'legacy-migration' },
    { path: '/search', component: SearchView, name: 'search' },
    { path: '/records', component: RecordsView, name: 'records' },
    { path: '/jobs', component: JobsView, name: 'jobs' },
    { path: '/audit', component: AuditView, name: 'audit' },
    { path: '/settings', component: SettingsView, name: 'settings' },
    { path: '/permissions', component: PermissionsView, name: 'permissions' },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})
