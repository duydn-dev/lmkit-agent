<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-4 md:p-6">
    <div class="max-w-6xl mx-auto">
      <!-- Page header -->
      <header class="mb-6 flex flex-wrap items-start gap-4">
        <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-blue-500 to-blue-600 flex items-center justify-center shadow-md shadow-blue-500/20 flex-shrink-0">
          <i class="pi pi-th-large text-white text-sm" aria-hidden="true"></i>
        </div>
        <div class="min-w-0 flex-1">
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">Bảng điều khiển quản trị</h1>
          <p class="text-sm text-gray-500">
            Tổng quan vận hành toàn hệ thống và lối tắt đến các khu vực quản trị.
          </p>
        </div>

        <!-- Bộ chọn kỳ: mọi chuỗi theo ngày trên trang đều theo giá trị này. -->
        <div class="flex items-center gap-2">
          <div
            class="inline-flex rounded-lg border border-gray-200 bg-white p-0.5"
            role="group"
            aria-label="Chọn kỳ thống kê"
          >
            <button
              v-for="option in periods"
              :key="option"
              type="button"
              class="rounded-md px-3 py-1.5 text-sm font-medium transition-colors focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              :class="days === option ? 'bg-blue-600 text-white' : 'text-gray-600 hover:bg-gray-100'"
              :aria-pressed="days === option"
              @click="setDays(option)"
            >
              {{ option }} ngày
            </button>
          </div>
          <button
            v-if="isAdmin"
            type="button"
            class="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 transition-colors hover:bg-gray-50 disabled:opacity-60 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            :disabled="exporting"
            @click="exportCsv"
          >
            <i class="pi pi-download text-xs" aria-hidden="true"></i>
            {{ exporting ? 'Đang xuất…' : 'Xuất CSV' }}
          </button>
        </div>
      </header>

      <p v-if="error" class="mb-6 rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-700" role="alert">
        Không tải được số liệu: {{ error }}
      </p>
      <p v-if="exportError" class="mb-6 rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-700" role="alert">
        {{ exportError }}
      </p>

      <!-- KPI cấp hệ thống -->
      <section aria-labelledby="kpi-heading" class="mb-6">
        <h2 id="kpi-heading" class="sr-only">Chỉ số chính</h2>
        <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
          <div v-for="card in kpiCards" :key="card.key" class="rounded-xl border border-gray-200 bg-white p-5">
            <div class="flex items-start justify-between gap-3">
              <div class="min-w-0">
                <p class="text-sm font-medium text-gray-600">{{ card.label }}</p>
                <p class="mt-2 text-3xl font-bold text-gray-900">
                  <span
                    v-if="loading"
                    class="inline-block h-8 w-16 rounded bg-gray-200 animate-pulse align-middle"
                    aria-hidden="true"
                  ></span>
                  <span v-else>{{ nf(card.value) }}</span>
                </p>
                <p v-if="card.hint" class="mt-1 text-xs text-gray-500">{{ card.hint }}</p>
              </div>
              <div :class="['w-10 h-10 rounded-xl flex items-center justify-center shadow-md flex-shrink-0', card.accent]">
                <i :class="card.icon" class="text-white text-sm" aria-hidden="true"></i>
              </div>
            </div>
          </div>
        </div>
      </section>

      <!-- Cảnh báo cần chú ý -->
      <section v-if="alerts.length" aria-labelledby="alerts-heading" class="mb-6">
        <h2 id="alerts-heading" class="text-sm font-semibold text-gray-900 mb-3">Cần chú ý</h2>
        <div class="rounded-xl border border-amber-200 bg-amber-50 p-5">
          <ul class="space-y-2">
            <li v-for="(alert, index) in alerts" :key="index" class="flex items-start gap-2 text-sm text-amber-900">
              <i class="pi pi-exclamation-triangle mt-0.5 text-amber-600" aria-hidden="true"></i>
              <span>{{ alert }}</span>
            </li>
          </ul>
        </div>
      </section>

      <!-- Biểu đồ -->
      <section v-if="cockpit" aria-labelledby="charts-heading" class="mb-6">
        <h2 id="charts-heading" class="text-sm font-semibold text-gray-900 mb-3">Diễn biến trong kỳ</h2>
        <div class="grid grid-cols-1 lg:grid-cols-2 gap-4">
          <div class="rounded-xl border border-gray-200 bg-white p-5 lg:col-span-2">
            <h3 class="text-sm font-semibold text-gray-900">Token theo ngày</h3>
            <p class="text-xs text-gray-500 mb-3">
              Prompt (dữ liệu gửi lên model) và completion (model sinh ra) của các lượt trả lời.
            </p>
            <div class="h-[280px]">
              <Chart type="bar" :data="tokenChartData" :options="stackedBarOptions" />
            </div>
          </div>

          <div class="rounded-xl border border-gray-200 bg-white p-5">
            <h3 class="text-sm font-semibold text-gray-900">Phân bổ token theo model</h3>
            <p class="text-xs text-gray-500 mb-3">Model nào đang tiêu tốn nhiều token nhất.</p>
            <div class="h-[280px]">
              <Chart v-if="modelChartData.labels.length" type="doughnut" :data="modelChartData" :options="doughnutOptions" />
              <p v-else class="text-sm text-gray-500">Chưa có lượt trả lời nào trong kỳ.</p>
            </div>
          </div>

          <div class="rounded-xl border border-gray-200 bg-white p-5">
            <h3 class="text-sm font-semibold text-gray-900">Người dùng hoạt động theo ngày</h3>
            <p class="text-xs text-gray-500 mb-3">
              Số người dùng riêng biệt có phiên chat mỗi ngày (không tính phiên chạy agent và chat tạm).
            </p>
            <div class="h-[280px]">
              <Chart type="bar" :data="usersChartData" :options="singleBarOptions" />
            </div>
          </div>
        </div>
      </section>

      <!-- Bảng số liệu -->
      <section v-if="cockpit" aria-labelledby="tables-heading" class="mb-6">
        <h2 id="tables-heading" class="text-sm font-semibold text-gray-900 mb-3">Chi tiết</h2>
        <div class="grid grid-cols-1 lg:grid-cols-2 gap-4">
          <!-- Top người dùng -->
          <div class="rounded-xl border border-gray-200 bg-white p-5">
            <div class="flex items-baseline justify-between gap-3 mb-3">
              <h3 class="text-sm font-semibold text-gray-900">Người dùng dùng nhiều nhất</h3>
              <span class="text-xs text-gray-500">
                DAU {{ nf(cockpit.users.dailyActive) }} · WAU {{ nf(cockpit.users.weeklyActive) }} · MAU {{ nf(cockpit.users.monthlyActive) }}
              </span>
            </div>
            <div v-if="cockpit.users.topUsers.length" class="overflow-x-auto">
              <table class="w-full text-sm">
                <thead>
                  <tr class="text-left text-xs uppercase tracking-wide text-gray-500">
                    <th scope="col" class="py-2 pr-3 font-medium">Người dùng</th>
                    <th scope="col" class="py-2 pr-3 font-medium text-right">Hỏi</th>
                    <th scope="col" class="py-2 pr-3 font-medium text-right">Trả lời</th>
                    <th scope="col" class="py-2 font-medium text-right">Token</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="user in cockpit.users.topUsers" :key="user.userId" class="border-t border-gray-100">
                    <td class="py-2 pr-3">
                      <span class="block truncate text-gray-900">{{ user.name || '(không có tên)' }}</span>
                      <span class="block truncate text-xs text-gray-500">{{ user.email }}</span>
                    </td>
                    <td class="py-2 pr-3 text-right text-gray-700">{{ nf(user.questions) }}</td>
                    <td class="py-2 pr-3 text-right text-gray-700">{{ nf(user.answers) }}</td>
                    <td class="py-2 text-right font-medium text-gray-900">
                      {{ nf(user.promptTokens + user.completionTokens) }}
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
            <p v-else class="text-sm text-gray-500">Chưa có lượt chat nào trong kỳ.</p>
          </div>

          <!-- Tập trung chi tiêu -->
          <div class="rounded-xl border border-gray-200 bg-white p-5">
            <h3 class="text-sm font-semibold text-gray-900 mb-3">Tập trung chi tiêu theo đơn vị</h3>
            <p class="text-2xl font-bold text-gray-900">{{ nf(cockpit.spend.totalTokens) }} token</p>
            <p class="text-xs text-gray-500 mt-1">
              Top 3 đơn vị chiếm {{ cockpit.spend.top3SharePct }}% · đơn vị lớn nhất chiếm
              {{ cockpit.spend.topTenantSharePct }}%
            </p>
            <p v-if="cockpit.spend.topTenantName" class="text-xs text-gray-500 mt-1">
              Nhiều nhất: <span class="text-gray-700">{{ cockpit.spend.topTenantName }}</span>
            </p>
            <ul class="mt-4 space-y-3">
              <li v-for="tenant in cockpit.spend.byTenant" :key="tenant.tenantId">
                <div class="flex items-baseline justify-between gap-3 text-sm">
                  <span class="min-w-0 truncate text-gray-700">{{ tenant.tenantName }}</span>
                  <span class="flex-shrink-0 text-gray-500">
                    {{ nf(tenant.promptTokens + tenant.completionTokens) }} · {{ sharePct(tenant) }}%
                  </span>
                </div>
                <div class="mt-1 h-1.5 w-full rounded-full bg-gray-100">
                  <div class="h-1.5 rounded-full bg-blue-600" :style="{ width: `${sharePct(tenant)}%` }"></div>
                </div>
              </li>
            </ul>
          </div>

          <!-- Hạn mức -->
          <div class="rounded-xl border border-gray-200 bg-white p-5">
            <h3 class="text-sm font-semibold text-gray-900 mb-3">Hạn mức &amp; số dư</h3>
            <dl class="grid grid-cols-2 gap-3 text-sm">
              <div>
                <dt class="text-gray-500">Đang dùng gói</dt>
                <dd class="font-semibold text-gray-900">
                  {{ nf(cockpit.quota.tenantsOnPlan) }} / {{ nf(cockpit.quota.tenantsWithoutPlan) }} chưa gán
                </dd>
              </div>
              <div>
                <dt class="text-gray-500">Hạn mức tháng</dt>
                <dd class="font-semibold text-gray-900">{{ nf(cockpit.quota.totalMonthlyLimit) }}</dd>
              </div>
              <div>
                <dt class="text-gray-500">Đã dùng tháng này</dt>
                <dd class="font-semibold text-gray-900">{{ nf(cockpit.quota.totalMonthlyUsed) }}</dd>
              </div>
              <div>
                <dt class="text-gray-500">Token mua trước</dt>
                <dd class="font-semibold text-gray-900">{{ nf(cockpit.quota.totalCreditBalance) }}</dd>
              </div>
            </dl>
            <ul class="mt-4 space-y-3">
              <li v-for="tenant in limitedTenants" :key="tenant.tenantId">
                <div class="flex items-baseline justify-between gap-3 text-sm">
                  <span class="min-w-0 truncate text-gray-700">{{ tenant.tenantName }}</span>
                  <span class="flex-shrink-0" :class="tenant.utilizationPct >= 100 ? 'text-red-600' : 'text-gray-500'">
                    {{ nf(tenant.usedTokens) }} / {{ nf(tenant.monthlyLimit) }} · {{ tenant.utilizationPct }}%
                  </span>
                </div>
                <div class="mt-1 h-1.5 w-full rounded-full bg-gray-100">
                  <div
                    class="h-1.5 rounded-full"
                    :class="tenant.utilizationPct >= 100 ? 'bg-red-600' : tenant.utilizationPct >= 80 ? 'bg-amber-500' : 'bg-emerald-600'"
                    :style="{ width: `${Math.min(100, tenant.utilizationPct)}%` }"
                  ></div>
                </div>
              </li>
            </ul>
            <p v-if="!limitedTenants.length" class="mt-3 text-sm text-gray-500">
              Chưa có đơn vị nào được gán gói có hạn mức.
            </p>
          </div>

          <!-- Tài liệu + hoạt động + hiệu năng -->
          <div class="rounded-xl border border-gray-200 bg-white p-5">
            <h3 class="text-sm font-semibold text-gray-900 mb-3">Cơ sở tri thức</h3>
            <dl class="grid grid-cols-2 gap-3 text-sm">
              <div>
                <dt class="text-gray-500">Tài liệu</dt>
                <dd class="font-semibold text-gray-900">{{ nf(cockpit.documents.total) }}</dd>
              </div>
              <div>
                <dt class="text-gray-500">Đã lập chỉ mục</dt>
                <dd class="font-semibold text-gray-900">
                  {{ nf(cockpit.documents.indexed) }} ({{ indexRatePct }}%)
                </dd>
              </div>
              <div>
                <dt class="text-gray-500">Đang chờ</dt>
                <dd class="font-semibold text-gray-900">{{ nf(cockpit.documents.pending) }}</dd>
              </div>
              <div>
                <dt class="text-gray-500">Lỗi</dt>
                <dd class="font-semibold" :class="cockpit.documents.failed ? 'text-red-600' : 'text-gray-900'">
                  {{ nf(cockpit.documents.failed) }}
                </dd>
              </div>
              <div>
                <dt class="text-gray-500">Đoạn (chunk)</dt>
                <dd class="font-semibold text-gray-900">{{ nf(cockpit.documents.totalChunks) }}</dd>
              </div>
              <div>
                <dt class="text-gray-500">Độ trễ trung bình</dt>
                <dd class="font-semibold text-gray-900">
                  {{ cockpit.performance.samples ? `${nf(cockpit.performance.avgLatencyMs)} ms` : '—' }}
                </dd>
              </div>
            </dl>
            <p class="mt-3 text-xs text-gray-500">
              p95 {{ cockpit.performance.samples ? `${nf(cockpit.performance.p95LatencyMs)} ms` : '—' }}
              trên {{ nf(cockpit.performance.samples) }} mẫu có đo độ trễ.
            </p>
            <h4 class="mt-4 text-xs font-semibold uppercase tracking-wide text-gray-500">Hành động nhiều nhất</h4>
            <ul class="mt-2 space-y-1.5 text-sm">
              <li v-for="action in cockpit.activity.topActions" :key="action.key" class="flex items-baseline justify-between gap-3">
                <span class="min-w-0 truncate text-gray-700">{{ action.key }}</span>
                <span class="flex-shrink-0 text-gray-500">{{ nf(action.count) }}</span>
              </li>
            </ul>
            <p v-if="!cockpit.activity.topActions.length" class="mt-2 text-sm text-gray-500">
              Chưa ghi nhận hoạt động nào trong kỳ.
            </p>
          </div>
        </div>
      </section>

      <!-- Sử dụng của chính người đang đăng nhập -->
      <section v-if="stats" aria-labelledby="self-heading" class="mb-6">
        <h2 id="self-heading" class="text-sm font-semibold text-gray-900 mb-3">Sử dụng của tôi</h2>
        <div class="rounded-xl border border-gray-200 bg-white p-5">
          <dl class="grid grid-cols-2 sm:grid-cols-4 gap-4 text-sm">
            <div>
              <dt class="text-gray-500">Câu hỏi</dt>
              <dd class="text-lg font-semibold text-gray-900">{{ nf(stats.myUsage.questions) }}</dd>
            </div>
            <div>
              <dt class="text-gray-500">Lượt trả lời</dt>
              <dd class="text-lg font-semibold text-gray-900">{{ nf(stats.myUsage.answers) }}</dd>
            </div>
            <div>
              <dt class="text-gray-500">Token</dt>
              <dd class="text-lg font-semibold text-gray-900">
                {{ nf(stats.myUsage.promptTokens + stats.myUsage.completionTokens) }}
              </dd>
            </div>
            <div>
              <dt class="text-gray-500">Phiên chat</dt>
              <dd class="text-lg font-semibold text-gray-900">{{ nf(stats.myUsage.sessions) }}</dd>
            </div>
          </dl>
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
            class="group flex items-start gap-4 rounded-xl border border-gray-200 bg-white p-5 transition-colors hover:border-blue-300 hover:bg-blue-50/40 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
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
import { computed, onMounted, ref } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';

// ─── Hợp đồng dữ liệu (khớp DashboardModels.cs của backend) ────────────────────
type PeriodDays = 7 | 30 | 90;
const periods = [7, 30, 90] as const satisfies readonly PeriodDays[];

interface DashboardTenantQuota {
  tenantId: string;
  tenantName: string;
  planName: string | null;
  monthlyLimit: number;
  usedTokens: number;
  utilizationPct: number;
  creditBalance: number;
  isUnlimited: boolean;
}

interface DashboardStats {
  periodDays: number;
  totalUsers: number;
  totalTenants: number;
  totalSessions: number;
  totalDocuments: number;
  myUsage: { questions: number; answers: number; promptTokens: number; completionTokens: number; sessions: number };
  // `cockpit` chỉ có khi caller là Admin — backend bỏ hẳn khối này cho Member.
  cockpit: {
    tokens: {
      promptTokens: number;
      completionTokens: number;
      messages: number;
      daily: { date: string; promptTokens: number; completionTokens: number; messages: number }[];
      byModel: { modelName: string; promptTokens: number; completionTokens: number; messages: number }[];
    };
    users: {
      dailyActive: number;
      weeklyActive: number;
      monthlyActive: number;
      totalUsers: number;
      newUsers: number;
      adoptionPct: number;
      daily: { date: string; count: number }[];
      topUsers: { userId: string; name: string; email: string; questions: number; answers: number; promptTokens: number; completionTokens: number }[];
    };
    spend: {
      totalTokens: number;
      top3SharePct: number;
      topTenantSharePct: number;
      topTenantName: string;
      byTenant: { tenantId: string; tenantName: string; promptTokens: number; completionTokens: number; messages: number }[];
    };
    quota: {
      tenantsOnPlan: number;
      tenantsOverThreshold: number;
      tenantsOverLimit: number;
      tenantsWithoutPlan: number;
      totalMonthlyLimit: number;
      totalMonthlyUsed: number;
      totalCreditBalance: number;
      byTenant: DashboardTenantQuota[];
    };
    documents: { total: number; indexed: number; pending: number; failed: number; totalChunks: number };
    activity: { total: number; daily: { date: string; count: number }[]; topActions: { key: string; count: number }[] };
    performance: { samples: number; avgLatencyMs: number; p95LatencyMs: number };
    alerts: {
      nearLimitTenants: number;
      overLimitTenants: number;
      expiringGrantCount: number;
      expiringGrants: { tenantId: string; tenantName: string; remainingTokens: number; expiresAtUtc: string; daysLeft: number }[];
    };
  } | null;
}

const days = ref<PeriodDays>(30);
const loading = ref(true);
const error = ref<string | null>(null);
const exportError = ref<string | null>(null);
const exporting = ref(false);
const stats = ref<DashboardStats | null>(null);

const cockpit = computed(() => stats.value?.cockpit ?? null);

// Nhận biết Admin ngay từ dữ liệu: backend CHỈ dựng khối `cockpit` cho Admin, nên có
// cockpit nghĩa là caller có quyền xuất CSV. Không cần gọi thêm endpoint vai trò — và
// cũng không thể lệch pha với đúng thứ backend đã cho phép.
const isAdmin = computed(() => cockpit.value !== null);

const nf = (value: number) => new Intl.NumberFormat('vi-VN').format(value ?? 0);

const api = ApiFactory.DASHBOARD;

const kpiCards = computed(() => {
  const s = stats.value;
  const tokens = cockpit.value ? cockpit.value.tokens.promptTokens + cockpit.value.tokens.completionTokens : 0;
  return [
    { key: 'users', label: 'Người dùng', value: s?.totalUsers ?? 0, icon: 'pi pi-users', accent: 'bg-gradient-to-br from-blue-500 to-blue-600', hint: '' },
    { key: 'tenants', label: 'Đơn vị', value: s?.totalTenants ?? 0, icon: 'pi pi-building', accent: 'bg-gradient-to-br from-rose-500 to-red-600', hint: '' },
    { key: 'sessions', label: 'Phiên chat', value: s?.totalSessions ?? 0, icon: 'pi pi-comments', accent: 'bg-gradient-to-br from-violet-500 to-violet-600', hint: '' },
    { key: 'documents', label: 'Tài liệu', value: s?.totalDocuments ?? 0, icon: 'pi pi-file', accent: 'bg-gradient-to-br from-emerald-500 to-emerald-600', hint: '' },
    { key: 'tokens', label: `Token ${days.value} ngày`, value: tokens, icon: 'pi pi-chart-bar', accent: 'bg-gradient-to-br from-indigo-500 to-blue-600', hint: 'Ước lượng, chỉ tính lượt trả lời' },
    { key: 'messages', label: 'Lượt trả lời', value: cockpit.value?.tokens.messages ?? 0, icon: 'pi pi-sparkles', accent: 'bg-gradient-to-br from-sky-500 to-cyan-600', hint: '' },
    { key: 'adoption', label: 'Độ phổ cập', value: cockpit.value?.users.adoptionPct ?? 0, icon: 'pi pi-percentage', accent: 'bg-gradient-to-br from-amber-500 to-orange-600', hint: 'MAU / tổng người dùng' },
    { key: 'latency', label: 'Độ trễ TB (ms)', value: cockpit.value?.performance.avgLatencyMs ?? 0, icon: 'pi pi-clock', accent: 'bg-gradient-to-br from-slate-500 to-slate-600', hint: cockpit.value ? `p95 ${nf(cockpit.value.performance.p95LatencyMs)} ms` : '' }
  ];
});

// Cảnh báo gộp từ nhiều nguồn khác nhau; mỗi mục là một câu người đọc hiểu ngay.
const alerts = computed(() => {
  const list: string[] = [];
  const c = cockpit.value;
  if (!c) return list;
  if (c.quota.tenantsOverLimit > 0) list.push(`${nf(c.quota.tenantsOverLimit)} đơn vị đã VƯỢT hạn mức token tháng này.`);
  if (c.quota.tenantsOverThreshold > 0) list.push(`${nf(c.quota.tenantsOverThreshold)} đơn vị đã dùng từ 80% hạn mức tháng.`);
  if (c.alerts.expiringGrantCount > 0) list.push(`${nf(c.alerts.expiringGrantCount)} khoản cấp thêm token sắp hết hạn trong 30 ngày.`);
  for (const grant of c.alerts.expiringGrants) {
    list.push(`"${grant.tenantName}" còn ${nf(grant.remainingTokens)} token cấp thêm, hết hạn sau ${grant.daysLeft} ngày.`);
  }
  if (c.documents.failed > 0) list.push(`${nf(c.documents.failed)} tài liệu lập chỉ mục thất bại.`);
  if (c.documents.pending > 0) list.push(`${nf(c.documents.pending)} tài liệu đang chờ lập chỉ mục.`);
  return list;
});

const indexRatePct = computed(() => {
  const d = cockpit.value?.documents;
  if (!d || d.total <= 0) return 0;
  return Math.round((d.indexed * 100) / d.total);
});

// Chỉ đơn vị có gói VÀ có hạn mức thật mới vẽ thanh % — gói không giới hạn không có gì để so.
const limitedTenants = computed(() => cockpit.value?.quota.byTenant.filter((t) => !t.isUnlimited) ?? []);

const sharePct = (tenant: { promptTokens: number; completionTokens: number }) => {
  const total = cockpit.value?.spend.totalTokens ?? 0;
  if (total <= 0) return 0;
  return Math.round(((tenant.promptTokens + tenant.completionTokens) * 100) / total);
};

const tokenChartData = computed(() => {
  const daily = cockpit.value?.tokens.daily ?? [];
  return {
    labels: daily.map((d) => d.date.slice(5)),
    datasets: [
      { label: 'Prompt', data: daily.map((d) => d.promptTokens), backgroundColor: '#3b82f6' },
      { label: 'Completion', data: daily.map((d) => d.completionTokens), backgroundColor: '#10b981' }
    ]
  };
});

const usersChartData = computed(() => {
  const daily = cockpit.value?.users.daily ?? [];
  return {
    labels: daily.map((d) => d.date.slice(5)),
    datasets: [{ label: 'Người dùng hoạt động', data: daily.map((d) => d.count), backgroundColor: '#8b5cf6' }]
  };
});

const modelChartData = computed(() => {
  const models = cockpit.value?.tokens.byModel ?? [];
  return {
    labels: models.map((m) => m.modelName),
    datasets: [
      {
        data: models.map((m) => m.promptTokens + m.completionTokens),
        backgroundColor: ['#3b82f6', '#10b981', '#f59e0b', '#8b5cf6', '#ef4444', '#14b8a6', '#64748b']
      }
    ]
  };
});

const stackedBarOptions = {
  responsive: true,
  maintainAspectRatio: false,
  plugins: { legend: { position: 'bottom' } },
  scales: { x: { stacked: true }, y: { stacked: true, beginAtZero: true } }
};

const singleBarOptions = {
  responsive: true,
  maintainAspectRatio: false,
  plugins: { legend: { display: false } },
  scales: { y: { beginAtZero: true, ticks: { precision: 0 } } }
};

const doughnutOptions = {
  responsive: true,
  maintainAspectRatio: false,
  plugins: { legend: { position: 'bottom' } }
};

const navCards = [
  { to: '/admin/users', icon: 'pi pi-users', accent: 'bg-gradient-to-br from-blue-500 to-blue-600', title: 'Quản lý tài khoản', description: 'Cấp tài khoản, phân quyền và khóa người dùng.' },
  { to: '/admin/mcp-servers', icon: 'pi pi-server', accent: 'bg-gradient-to-br from-emerald-500 to-emerald-600', title: 'Máy chủ MCP', description: 'Kết nối và quản lý máy chủ Model Context Protocol.' },
  { to: '/admin/knowledge', icon: 'pi pi-database', accent: 'bg-gradient-to-br from-violet-500 to-violet-600', title: 'Cơ sở tri thức', description: 'Quản lý nguồn tri thức dùng chung cho tenant.' },
  { to: '/admin/databases', icon: 'pi pi-table', accent: 'bg-gradient-to-br from-indigo-500 to-blue-600', title: 'Kết nối CSDL', description: 'Kết nối cơ sở dữ liệu ngoài để agent truy vấn và lập chỉ mục lược đồ.' },
  { to: '/admin/tenants', icon: 'pi pi-building', accent: 'bg-gradient-to-br from-rose-500 to-red-600', title: 'Quản lý Tenant', description: 'Quản lý đơn vị/tổ chức sử dụng hệ thống.' },
  { to: '/admin/lora', icon: 'pi pi-sliders-h', accent: 'bg-gradient-to-br from-fuchsia-500 to-purple-600', title: 'LoRA Adapters', description: 'Đăng ký adapter tinh chỉnh hot-swap cho model chat.' },
  { to: '/admin/audit', icon: 'pi pi-shield', accent: 'bg-gradient-to-br from-slate-500 to-slate-600', title: 'Nhật ký hoạt động', description: 'Theo dõi hoạt động của agent và hệ thống.' },
  { to: '/admin/widget', icon: 'pi pi-objects-column', accent: 'bg-gradient-to-br from-blue-500 to-blue-700', title: 'Widget nhúng', description: 'Bật widget chat công khai, cho phép origin và quản lý khóa.' },
  { to: '/approvals', icon: 'pi pi-check-square', accent: 'bg-gradient-to-br from-amber-500 to-orange-600', title: 'Phê duyệt tác vụ', description: 'Xem xét và duyệt các tác vụ đang chờ.' }
];

const load = async () => {
  loading.value = true;
  error.value = null;
  try {
    const response = await http.get(api.STATS(days.value));
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    stats.value = (await response.json()) as DashboardStats;
  } catch (err) {
    stats.value = null;
    error.value = err instanceof Error ? err.message : 'lỗi không xác định';
  } finally {
    loading.value = false;
  }
};

const setDays = (value: PeriodDays) => {
  if (days.value === value) return;
  days.value = value;
  void load();
};

const exportCsv = async () => {
  exportError.value = null;
  exporting.value = true;
  try {
    const response = await http.get(api.EXPORT_CSV(days.value));
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const blob = await response.blob();
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `bao-cao-su-dung-${days.value}ngay.csv`;
    link.click();
    URL.revokeObjectURL(url);
  } catch (err) {
    exportError.value = `Không xuất được báo cáo: ${err instanceof Error ? err.message : 'lỗi không xác định'}`;
  } finally {
    exporting.value = false;
  }
};

onMounted(() => {
  void load();
});
</script>
