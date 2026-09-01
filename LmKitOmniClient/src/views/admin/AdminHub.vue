<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-4 md:p-6">
    <div class="max-w-6xl mx-auto">
      <!-- Page header -->
      <header class="mb-6 flex items-center gap-4">
        <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-sky-500 to-blue-600 flex items-center justify-center shadow-md shadow-sky-500/20 flex-shrink-0">
          <i class="pi pi-th-large text-white text-sm" aria-hidden="true"></i>
        </div>
        <div>
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">Bảng điều khiển quản trị</h1>
          <p class="text-sm text-gray-500">Tổng quan hệ thống và lối tắt đến các khu vực quản trị của tenant.</p>
        </div>
      </header>

      <!-- Stat cards -->
      <section aria-labelledby="admin-stats-heading" class="mb-8">
        <h2 id="admin-stats-heading" class="sr-only">Số liệu tổng quan</h2>
        <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
          <div
            v-for="card in statCards"
            :key="card.key"
            class="rounded-xl border border-gray-200 bg-white p-5"
          >
            <div class="flex items-start justify-between gap-3">
              <div class="min-w-0">
                <p class="text-sm font-medium text-gray-600">{{ card.label }}</p>
                <p class="mt-2 text-3xl font-bold text-gray-900" aria-live="polite">
                  <span
                    v-if="statState[card.key] === 'loading'"
                    class="inline-block h-8 w-14 rounded bg-gray-200 animate-pulse align-middle"
                    aria-hidden="true"
                  ></span>
                  <span v-else-if="statState[card.key] === 'error'">—</span>
                  <span v-else>{{ statValue[card.key] }}</span>
                </p>
              </div>
              <div :class="['w-10 h-10 rounded-xl flex items-center justify-center shadow-md flex-shrink-0', card.accent]">
                <i :class="card.icon" class="text-white text-sm" aria-hidden="true"></i>
              </div>
            </div>
          </div>
        </div>
      </section>

      <!-- Navigation cards -->
      <section aria-labelledby="admin-nav-heading">
        <h2 id="admin-nav-heading" class="text-sm font-semibold text-gray-900 mb-3">Khu vực quản trị</h2>
        <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
          <router-link
            v-for="nav in navCards"
            :key="nav.to"
            :to="nav.to"
            class="group flex items-start gap-4 rounded-xl border border-gray-200 bg-white p-5 transition-colors hover:border-sky-300 hover:bg-sky-50/40 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sky-600"
          >
            <div :class="['w-10 h-10 rounded-xl flex items-center justify-center flex-shrink-0', nav.accent]">
              <i :class="nav.icon" class="text-white text-sm" aria-hidden="true"></i>
            </div>
            <div class="min-w-0">
              <div class="flex items-center gap-1.5">
                <span class="text-sm font-semibold text-gray-900">{{ nav.title }}</span>
                <i class="pi pi-arrow-right text-xs text-gray-400 transition-transform group-hover:translate-x-0.5" aria-hidden="true"></i>
              </div>
              <p class="text-sm text-gray-500 mt-1">{{ nav.description }}</p>
            </div>
          </router-link>
        </div>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';

type StatKey = 'users' | 'documents' | 'mcp' | 'pending';
type StatStatus = 'loading' | 'ok' | 'error';

// Each card's count is loaded independently; a single failure shows "—" for that
// card only (never an error banner), so the dashboard always renders.
const statCards = [
  { key: 'users', label: 'Người dùng', icon: 'pi pi-users', accent: 'bg-gradient-to-br from-sky-500 to-sky-600', url: '/api/users' },
  { key: 'documents', label: 'Tài liệu', icon: 'pi pi-file', accent: 'bg-gradient-to-br from-violet-500 to-violet-600', url: ApiFactory.DOCUMENT.BASE },
  { key: 'mcp', label: 'Máy chủ MCP', icon: 'pi pi-server', accent: 'bg-gradient-to-br from-emerald-500 to-emerald-600', url: ApiFactory.MCP.BASE },
  { key: 'pending', label: 'Chờ phê duyệt', icon: 'pi pi-check-square', accent: 'bg-gradient-to-br from-amber-500 to-orange-600', url: ApiFactory.TASK_APPROVAL.PENDING }
] as const satisfies ReadonlyArray<{ key: StatKey; label: string; icon: string; accent: string; url: string }>;

const navCards = [
  { to: '/admin/users', icon: 'pi pi-users', accent: 'bg-gradient-to-br from-sky-500 to-sky-600', title: 'Quản lý User', description: 'Cấp tài khoản, phân quyền và khóa người dùng.' },
  { to: '/admin/mcp-servers', icon: 'pi pi-server', accent: 'bg-gradient-to-br from-emerald-500 to-emerald-600', title: 'Máy chủ MCP', description: 'Kết nối và quản lý máy chủ Model Context Protocol.' },
  { to: '/admin/knowledge', icon: 'pi pi-database', accent: 'bg-gradient-to-br from-violet-500 to-violet-600', title: 'Cơ sở tri thức', description: 'Quản lý nguồn tri thức dùng chung cho tenant.' },
  { to: '/admin/audit', icon: 'pi pi-shield', accent: 'bg-gradient-to-br from-slate-500 to-slate-600', title: 'Nhật ký hoạt động', description: 'Theo dõi hoạt động của agent và hệ thống.' },
  { to: '/approvals', icon: 'pi pi-check-square', accent: 'bg-gradient-to-br from-amber-500 to-orange-600', title: 'Phê duyệt tác vụ', description: 'Xem xét và duyệt các tác vụ đang chờ.' }
];

const statState = ref<Record<StatKey, StatStatus>>({ users: 'loading', documents: 'loading', mcp: 'loading', pending: 'loading' });
const statValue = ref<Record<StatKey, number>>({ users: 0, documents: 0, mcp: 0, pending: 0 });

const fetchCount = async (url: string): Promise<number> => {
  const response = await http.get(url);
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  const data = await response.json();
  return Array.isArray(data) ? data.length : 0;
};

const loadStats = async () => {
  const results = await Promise.allSettled(statCards.map((card) => fetchCount(card.url)));
  results.forEach((result, index) => {
    const key = statCards[index].key;
    if (result.status === 'fulfilled') {
      statValue.value[key] = result.value;
      statState.value[key] = 'ok';
    } else {
      statState.value[key] = 'error';
      console.warn(`[admin-hub] không thể tải số liệu "${key}"`, result.reason);
    }
  });
};

onMounted(() => {
  void loadStats();
});
</script>
