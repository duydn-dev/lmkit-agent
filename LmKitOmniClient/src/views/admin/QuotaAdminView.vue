<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-4 md:p-6">
    <div class="max-w-6xl mx-auto">
      <header class="mb-4 flex items-center gap-4">
        <div class="w-10 h-10 rounded-lg bg-gov-blue-dark flex items-center justify-center shadow-md flex-shrink-0">
          <i class="pi pi-wallet text-white text-sm" aria-hidden="true"></i>
        </div>
        <div>
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">Hạn mức &amp; Token</h1>
          <p class="text-sm text-gray-500">
            Gán gói, cấp thêm token và số dư cho từng đơn vị. Đã dùng tính cả lượt chat lẫn lần chạy agent.
          </p>
        </div>
      </header>

      <div v-if="error" role="alert" class="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {{ error }}
      </div>

      <!-- ------------------------------------------------------------------ Gói -->
      <section class="mb-8">
        <div class="mb-3 flex flex-wrap items-center justify-between gap-3">
          <h2 class="text-base font-semibold text-gray-900">Gói hạn mức</h2>
          <div class="flex items-center gap-2">
            <Button icon="pi pi-refresh" severity="secondary" outlined :loading="loading" aria-label="Tải lại" @click="reload" />
            <Button label="Thêm gói" icon="pi pi-plus" @click="openCreatePlan" />
          </div>
        </div>

        <DataTable :value="plans" :loading="loading" dataKey="id" class="bg-white rounded-lg border border-gray-200 overflow-hidden">
          <template #empty>
            <div class="p-8 text-center text-gray-500 text-sm">
              <i class="pi pi-wallet text-3xl text-gray-300 block mb-3" aria-hidden="true"></i>
              Chưa có gói nào — thêm gói đầu tiên để bắt đầu gán hạn mức cho đơn vị.
            </div>
          </template>
          <Column field="name" header="Tên gói" />
          <Column header="Hạn mức tháng" :style="{ width: '170px' }">
            <template #body="{ data }">
              <span class="text-sm font-medium text-gray-900">{{ limitLabel(data.monthlyTokenLimit) }}</span>
            </template>
          </Column>
          <Column header="Đơn vị đang dùng" :style="{ width: '170px' }">
            <template #body="{ data }">
              <Tag :value="`${data.tenantCount} đơn vị`" :severity="data.tenantCount > 0 ? 'info' : 'secondary'" />
            </template>
          </Column>
          <Column header="Trạng thái" :style="{ width: '130px' }">
            <template #body="{ data }">
              <Tag :value="data.isActive ? 'Đang dùng' : 'Đã ngừng'" :severity="data.isActive ? 'success' : 'secondary'" />
            </template>
          </Column>
          <Column header="Thao tác" :style="{ width: '130px' }">
            <template #body="{ data }">
              <div class="flex items-center gap-1">
                <Button icon="pi pi-pencil" text rounded severity="secondary" aria-label="Sửa gói" @click="openEditPlan(data)" />
                <Button
                  v-if="data.isActive"
                  icon="pi pi-ban"
                  text
                  rounded
                  severity="danger"
                  aria-label="Ngừng dùng gói"
                  @click="confirmDeactivatePlan(data)" />
              </div>
            </template>
          </Column>
        </DataTable>
      </section>

      <!-- ------------------------------------------------------------------ Đơn vị -->
      <section>
        <h2 class="mb-3 text-base font-semibold text-gray-900">Hạn mức theo đơn vị</h2>

        <DataTable :value="tenants" :loading="loading" dataKey="tenantId" class="bg-white rounded-lg border border-gray-200 overflow-hidden">
          <template #empty>
            <div class="p-8 text-center text-gray-500 text-sm">Chưa có đơn vị nào.</div>
          </template>
          <Column field="tenantName" header="Đơn vị" />
          <Column header="Gói" :style="{ width: '170px' }">
            <template #body="{ data }">
              <Tag v-if="data.planName" :value="data.planName" severity="info" />
              <span v-else class="text-sm text-gray-400">Chưa gán</span>
            </template>
          </Column>
          <Column header="Đã dùng / Hạn mức" :style="{ width: '260px' }">
            <template #body="{ data }">
              <div v-if="data.isUnlimited" class="text-sm text-gray-500">
                {{ nf(data.usedTokens) }} token · <span class="text-gray-400">không giới hạn</span>
              </div>
              <div v-else>
                <div class="mb-1 flex items-center justify-between gap-2 text-xs">
                  <span class="font-medium text-gray-900">{{ nf(data.usedTokens) }} / {{ nf(data.monthlyTokenLimit) }}</span>
                  <span :class="utilizationClass(data.utilizationPct)">{{ data.utilizationPct }}%</span>
                </div>
                <ProgressBar :value="Math.min(data.utilizationPct, 100)" :showValue="false" :style="{ height: '6px' }" />
              </div>
            </template>
          </Column>
          <Column header="Grant còn lại" :style="{ width: '150px' }">
            <template #body="{ data }">
              <span v-if="data.activeGrantCount > 0" class="text-sm text-gray-700">
                +{{ nf(data.grantRemainingTokens) }}
                <span class="text-xs text-gray-400">({{ data.activeGrantCount }})</span>
              </span>
              <span v-else class="text-sm text-gray-400">—</span>
            </template>
          </Column>
          <Column header="Số dư" :style="{ width: '120px' }">
            <template #body="{ data }"><span class="text-sm text-gray-700">{{ nf(data.creditBalance) }}</span></template>
          </Column>
          <Column header="Thao tác" :style="{ width: '190px' }">
            <template #body="{ data }">
              <div class="flex items-center gap-1">
                <Button label="Gán gói" icon="pi pi-link" size="small" text @click="openAssign(data)" />
                <Button
                  v-if="data.planId"
                  icon="pi pi-times-circle"
                  text
                  rounded
                  severity="danger"
                  aria-label="Gỡ gói"
                  @click="confirmRemovePlan(data)" />
                <Button icon="pi pi-plus-circle" text rounded severity="secondary" aria-label="Cấp thêm token" @click="openGrants(data)" />
                <Button icon="pi pi-credit-card" text rounded severity="secondary" aria-label="Đặt số dư" @click="openCredit(data)" />
              </div>
            </template>
          </Column>
        </DataTable>
      </section>
    </div>

    <!-- ------------------------------------------------------------------ Dialog: gói -->
    <Dialog v-model:visible="planDialog" modal :header="planForm.id ? 'Sửa gói' : 'Thêm gói'" class="w-[28rem] max-w-[95vw]">
      <form class="grid gap-3" @submit.prevent="savePlan">
        <div v-if="formError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
          {{ formError }}
        </div>
        <div class="grid gap-1">
          <label for="plan-name" class="text-sm font-medium text-gray-700">Tên gói <span class="text-red-500">*</span></label>
          <InputText id="plan-name" v-model="planForm.name" maxlength="200" placeholder="Ví dụ: Gói 1 triệu token" class="w-full" />
        </div>
        <div class="grid gap-1">
          <label for="plan-limit" class="text-sm font-medium text-gray-700">Hạn mức token mỗi tháng</label>
          <InputNumber id="plan-limit" v-model="planForm.monthlyTokenLimit" :min="0" :useGrouping="true" class="w-full" />
          <small class="text-xs text-gray-400">0 = không giới hạn (chỉ dùng cho gói nội bộ).</small>
        </div>
        <div class="flex items-center gap-2">
          <InputSwitch v-model="planForm.isActive" inputId="plan-active" />
          <label for="plan-active" class="text-sm text-gray-700">Đang dùng (gói đã ngừng sẽ không gán được cho đơn vị)</label>
        </div>
      </form>
      <template #footer>
        <Button label="Hủy" severity="secondary" outlined @click="planDialog = false" />
        <Button label="Lưu" icon="pi pi-check" :loading="saving" @click="savePlan" />
      </template>
    </Dialog>

    <!-- ------------------------------------------------------------------ Dialog: gán gói -->
    <Dialog v-model:visible="assignDialog" modal header="Gán gói cho đơn vị" class="w-[28rem] max-w-[95vw]">
      <form class="grid gap-3" @submit.prevent="saveAssignment">
        <div v-if="formError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
          {{ formError }}
        </div>
        <p class="text-sm text-gray-500">
          Đơn vị: <span class="font-medium text-gray-900">{{ assignForm.tenantName }}</span>
        </p>
        <div class="grid gap-1">
          <label for="assign-plan" class="text-sm font-medium text-gray-700">Gói <span class="text-red-500">*</span></label>
          <Select
            id="assign-plan"
            v-model="assignForm.planId"
            :options="assignablePlans"
            optionLabel="name"
            optionValue="id"
            placeholder="Chọn gói"
            class="w-full">
            <template #option="{ option }">
              <span>{{ option.name }} · {{ limitLabel(option.monthlyTokenLimit) }}</span>
            </template>
          </Select>
        </div>
        <div class="grid gap-1">
          <label for="assign-renewal" class="text-sm font-medium text-gray-700">Ngày gia hạn (tuỳ chọn)</label>
          <DatePicker id="assign-renewal" v-model="assignForm.renewal" dateFormat="dd/mm/yy" showIcon showButtonBar class="w-full" />
          <small class="text-xs text-gray-400">Bỏ trống = không đặt kỳ hạn. Phải ở tương lai nếu có.</small>
        </div>
      </form>
      <template #footer>
        <Button label="Hủy" severity="secondary" outlined @click="assignDialog = false" />
        <Button label="Gán gói" icon="pi pi-check" :loading="saving" @click="saveAssignment" />
      </template>
    </Dialog>

    <!-- ------------------------------------------------------------------ Dialog: số dư -->
    <Dialog v-model:visible="creditDialog" modal header="Số dư token mua trước" class="w-[26rem] max-w-[95vw]">
      <form class="grid gap-3" @submit.prevent="saveCredit">
        <div v-if="formError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
          {{ formError }}
        </div>
        <p class="text-sm text-gray-500">
          Đơn vị: <span class="font-medium text-gray-900">{{ creditTenantName }}</span>
        </p>
        <div class="grid gap-1">
          <label for="credit-balance" class="text-sm font-medium text-gray-700">Số dư</label>
          <InputNumber id="credit-balance" v-model="creditBalance" :min="0" :useGrouping="true" class="w-full" />
          <small class="text-xs text-gray-400">Số dư là tài sản mua trước, không reset theo tháng như hạn mức gói.</small>
        </div>
      </form>
      <template #footer>
        <Button label="Hủy" severity="secondary" outlined @click="creditDialog = false" />
        <Button label="Lưu" icon="pi pi-check" :loading="saving" @click="saveCredit" />
      </template>
    </Dialog>

    <!-- ------------------------------------------------------------------ Dialog: grant -->
    <Dialog v-model:visible="grantsDialog" modal header="Token cấp thêm (grant)" class="w-[42rem] max-w-[95vw]">
      <div class="grid gap-4">
        <div v-if="formError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
          {{ formError }}
        </div>
        <p class="text-sm text-gray-500">
          Đơn vị: <span class="font-medium text-gray-900">{{ grantsTenantName }}</span>
        </p>

        <div class="rounded-lg border border-gray-200 p-3">
          <div class="mb-2 text-sm font-medium text-gray-700">Cấp thêm</div>
          <div class="grid gap-2 md:grid-cols-[140px_1fr_1fr_auto] md:items-start">
            <InputNumber v-model="grantForm.tokens" :min="1" :useGrouping="true" placeholder="Số token" class="w-full" />
            <DatePicker v-model="grantForm.expiresAt" dateFormat="dd/mm/yy" showIcon placeholder="Hết hạn (bỏ trống = vô hạn)" class="w-full" />
            <InputText v-model="grantForm.reason" maxlength="300" placeholder="Lý do (tuỳ chọn)" class="w-full" />
            <Button label="Cấp" icon="pi pi-plus" :loading="saving" @click="saveGrant" />
          </div>
        </div>

        <DataTable :value="grants" :loading="grantsLoading" dataKey="id" class="border border-gray-200 rounded-lg overflow-hidden">
          <template #empty>
            <div class="p-6 text-center text-gray-500 text-sm">Đơn vị chưa được cấp thêm token nào.</div>
          </template>
          <Column header="Cấp" :style="{ width: '130px' }">
            <template #body="{ data }"><span class="text-sm font-medium text-gray-900">{{ nf(data.tokens) }}</span></template>
          </Column>
          <Column header="Còn lại" :style="{ width: '130px' }">
            <template #body="{ data }">
              <span class="text-sm" :class="data.isExpired ? 'text-gray-400 line-through' : 'text-gray-700'">{{ nf(data.remainingTokens) }}</span>
            </template>
          </Column>
          <Column header="Hết hạn" :style="{ width: '130px' }">
            <template #body="{ data }">
              <Tag v-if="!data.expiresAtUtc" value="Vô hạn" severity="secondary" />
              <Tag v-else :value="dateLabel(data.expiresAtUtc)" :severity="data.isExpired ? 'danger' : 'info'" />
            </template>
          </Column>
          <Column field="reason" header="Lý do">
            <template #body="{ data }"><span class="text-sm text-gray-600">{{ data.reason || '—' }}</span></template>
          </Column>
          <Column header="" :style="{ width: '60px' }">
            <template #body="{ data }">
              <Button
                icon="pi pi-trash"
                text
                rounded
                severity="danger"
                :disabled="data.usedTokens > 0"
                :aria-label="data.usedTokens > 0 ? 'Grant đã dùng — không thể gỡ' : 'Gỡ grant'"
                @click="confirmDeleteGrant(data)" />
            </template>
          </Column>
        </DataTable>
      </div>
      <template #footer>
        <Button label="Đóng" severity="secondary" outlined @click="grantsDialog = false" />
      </template>
    </Dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { useConfirm } from 'primevue/useconfirm';
import { useToast } from 'primevue/usetoast';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';

interface PlanRow {
  id: string;
  name: string;
  monthlyTokenLimit: number;
  isActive: boolean;
  createdAtUtc: string;
  tenantCount: number;
}

interface TenantQuotaRow {
  tenantId: string;
  tenantName: string;
  subscriptionId?: string | null;
  planId?: string | null;
  planName?: string | null;
  monthlyTokenLimit: number;
  renewalAtUtc?: string | null;
  usedTokens: number;
  utilizationPct: number;
  isUnlimited: boolean;
  creditBalance: number;
  activeGrantCount: number;
  grantRemainingTokens: number;
}

interface GrantRow {
  id: string;
  tenantId: string;
  tokens: number;
  usedTokens: number;
  remainingTokens: number;
  expiresAtUtc?: string | null;
  reason?: string | null;
  createdAtUtc: string;
  isExpired: boolean;
}

const confirm = useConfirm();
const toast = useToast();

const plans = ref<PlanRow[]>([]);
const tenants = ref<TenantQuotaRow[]>([]);
const loading = ref(false);
const saving = ref(false);
const error = ref('');
const formError = ref('');

const nf = (value: number): string => new Intl.NumberFormat('vi-VN').format(value ?? 0);

/** 0 là "không giới hạn", không phải gói 0 token — hiển thị đúng nghĩa để không ai gán nhầm. */
const limitLabel = (limit: number): string => (limit > 0 ? `${nf(limit)} token` : 'Không giới hạn');

const dateLabel = (value: string): string => {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleDateString('vi-VN');
};

/** >100% là đã vượt hạn mức: đỏ, không kẹp về 100 ở nhãn (thanh thì kẹp để không tràn). */
const utilizationClass = (percent: number): string => {
  if (percent >= 100) return 'font-semibold text-red-600';
  if (percent >= 80) return 'font-semibold text-amber-600';
  return 'text-gray-500';
};

const reload = async () => {
  loading.value = true;
  error.value = '';
  try {
    const [plansResponse, tenantsResponse] = await Promise.all([
      http.get(ApiFactory.QUOTA.PLANS),
      http.get(ApiFactory.QUOTA.TENANTS)
    ]);
    if (!plansResponse.ok) {
      error.value = await readApiError(plansResponse, 'Không thể tải danh sách gói');
      return;
    }
    if (!tenantsResponse.ok) {
      error.value = await readApiError(tenantsResponse, 'Không thể tải hạn mức theo đơn vị');
      return;
    }
    plans.value = (await plansResponse.json()) as PlanRow[];
    tenants.value = (await tenantsResponse.json()) as TenantQuotaRow[];
  } catch (cause) {
    error.value = errorMessage(cause, 'Không thể tải dữ liệu hạn mức.');
  } finally {
    loading.value = false;
  }
};

// ---------------------------------------------------------------- Gói

const planDialog = ref(false);
const planForm = ref<{ id: string | null; name: string; monthlyTokenLimit: number; isActive: boolean }>({
  id: null,
  name: '',
  monthlyTokenLimit: 0,
  isActive: true
});

const openCreatePlan = () => {
  planForm.value = { id: null, name: '', monthlyTokenLimit: 0, isActive: true };
  formError.value = '';
  planDialog.value = true;
};

const openEditPlan = (plan: PlanRow) => {
  planForm.value = {
    id: plan.id,
    name: plan.name,
    monthlyTokenLimit: plan.monthlyTokenLimit,
    isActive: plan.isActive
  };
  formError.value = '';
  planDialog.value = true;
};

const savePlan = async () => {
  formError.value = '';
  const name = planForm.value.name.trim();
  if (!name) {
    formError.value = 'Vui lòng nhập tên gói.';
    return;
  }

  const payload = {
    name,
    monthlyTokenLimit: planForm.value.monthlyTokenLimit ?? 0,
    isActive: planForm.value.isActive
  };

  saving.value = true;
  try {
    const response = planForm.value.id
      ? await http.put(ApiFactory.QUOTA.PLAN_BY_ID(planForm.value.id), payload)
      : await http.post(ApiFactory.QUOTA.PLANS, payload);
    if (!response.ok) {
      formError.value = await readApiError(response, 'Không thể lưu gói');
      return;
    }
    planDialog.value = false;
    toast.add({ severity: 'success', summary: planForm.value.id ? 'Đã lưu gói' : 'Đã thêm gói', life: 3000 });
    await reload();
  } catch (cause) {
    formError.value = errorMessage(cause, 'Không thể lưu gói.');
  } finally {
    saving.value = false;
  }
};

const confirmDeactivatePlan = (plan: PlanRow) => {
  confirm.require({
    message: `Ngừng dùng gói "${plan.name}"? Các đơn vị phải được chuyển sang gói khác trước.`,
    header: 'Ngừng dùng gói',
    icon: 'pi pi-exclamation-triangle',
    acceptLabel: 'Ngừng dùng',
    rejectLabel: 'Hủy',
    acceptProps: { severity: 'danger' },
    accept: async () => {
      try {
        const response = await http.delete(ApiFactory.QUOTA.PLAN_BY_ID(plan.id));
        if (!response.ok) {
          toast.add({ severity: 'error', summary: 'Không thể ngừng dùng gói', detail: await readApiError(response, ''), life: 5000 });
          return;
        }
        toast.add({ severity: 'success', summary: 'Đã ngừng dùng gói', life: 3000 });
        await reload();
      } catch (cause) {
        toast.add({ severity: 'error', summary: 'Lỗi', detail: errorMessage(cause, 'Không thể ngừng dùng gói.'), life: 5000 });
      }
    }
  });
};

// ---------------------------------------------------------------- Gán gói

const assignDialog = ref(false);
const assignForm = ref<{ tenantId: string; tenantName: string; planId: string | null; renewal: Date | null }>({
  tenantId: '',
  tenantName: '',
  planId: null,
  renewal: null
});

/** Gói đã ngừng dùng không gán được (server cũng chặn) — không hiện ra để khỏi chọn nhầm. */
const assignablePlans = computed(() => plans.value.filter((plan) => plan.isActive));

const openAssign = (row: TenantQuotaRow) => {
  assignForm.value = {
    tenantId: row.tenantId,
    tenantName: row.tenantName,
    planId: row.planId ?? null,
    renewal: row.renewalAtUtc ? new Date(row.renewalAtUtc) : null
  };
  formError.value = '';
  assignDialog.value = true;
};

const saveAssignment = async () => {
  formError.value = '';
  if (!assignForm.value.planId) {
    formError.value = 'Vui lòng chọn gói.';
    return;
  }

  saving.value = true;
  try {
    const response = await http.put(ApiFactory.QUOTA.TENANT_PLAN(assignForm.value.tenantId), {
      planId: assignForm.value.planId,
      renewalAtUtc: assignForm.value.renewal ? assignForm.value.renewal.toISOString() : null
    });
    if (!response.ok) {
      formError.value = await readApiError(response, 'Không thể gán gói');
      return;
    }
    assignDialog.value = false;
    toast.add({ severity: 'success', summary: 'Đã gán gói cho đơn vị', life: 3000 });
    await reload();
  } catch (cause) {
    formError.value = errorMessage(cause, 'Không thể gán gói.');
  } finally {
    saving.value = false;
  }
};

const confirmRemovePlan = (row: TenantQuotaRow) => {
  confirm.require({
    message: `Gỡ gói khỏi "${row.tenantName}"? Đơn vị sẽ không còn hạn mức tháng.`,
    header: 'Gỡ gói',
    icon: 'pi pi-exclamation-triangle',
    acceptLabel: 'Gỡ gói',
    rejectLabel: 'Hủy',
    acceptProps: { severity: 'danger' },
    accept: async () => {
      try {
        const response = await http.delete(ApiFactory.QUOTA.TENANT_PLAN(row.tenantId));
        if (!response.ok) {
          toast.add({ severity: 'error', summary: 'Không thể gỡ gói', detail: await readApiError(response, ''), life: 5000 });
          return;
        }
        toast.add({ severity: 'success', summary: 'Đã gỡ gói', life: 3000 });
        await reload();
      } catch (cause) {
        toast.add({ severity: 'error', summary: 'Lỗi', detail: errorMessage(cause, 'Không thể gỡ gói.'), life: 5000 });
      }
    }
  });
};

// ---------------------------------------------------------------- Số dư

const creditDialog = ref(false);
const creditTenantId = ref('');
const creditTenantName = ref('');
const creditBalance = ref(0);

const openCredit = (row: TenantQuotaRow) => {
  creditTenantId.value = row.tenantId;
  creditTenantName.value = row.tenantName;
  creditBalance.value = row.creditBalance;
  formError.value = '';
  creditDialog.value = true;
};

const saveCredit = async () => {
  formError.value = '';
  saving.value = true;
  try {
    const response = await http.put(ApiFactory.QUOTA.TENANT_CREDIT(creditTenantId.value), {
      balance: creditBalance.value ?? 0
    });
    if (!response.ok) {
      formError.value = await readApiError(response, 'Không thể lưu số dư');
      return;
    }
    creditDialog.value = false;
    toast.add({ severity: 'success', summary: 'Đã lưu số dư', life: 3000 });
    await reload();
  } catch (cause) {
    formError.value = errorMessage(cause, 'Không thể lưu số dư.');
  } finally {
    saving.value = false;
  }
};

// ---------------------------------------------------------------- Grant

const grantsDialog = ref(false);
const grantsTenantId = ref('');
const grantsTenantName = ref('');
const grants = ref<GrantRow[]>([]);
const grantsLoading = ref(false);
const grantForm = ref<{ tokens: number | null; expiresAt: Date | null; reason: string }>({
  tokens: null,
  expiresAt: null,
  reason: ''
});

const loadGrants = async () => {
  grantsLoading.value = true;
  try {
    const response = await http.get(ApiFactory.QUOTA.TENANT_GRANTS(grantsTenantId.value));
    if (!response.ok) {
      formError.value = await readApiError(response, 'Không thể tải danh sách grant');
      return;
    }
    grants.value = (await response.json()) as GrantRow[];
  } catch (cause) {
    formError.value = errorMessage(cause, 'Không thể tải danh sách grant.');
  } finally {
    grantsLoading.value = false;
  }
};

const openGrants = async (row: TenantQuotaRow) => {
  grantsTenantId.value = row.tenantId;
  grantsTenantName.value = row.tenantName;
  grantForm.value = { tokens: null, expiresAt: null, reason: '' };
  formError.value = '';
  grantsDialog.value = true;
  await loadGrants();
};

const saveGrant = async () => {
  formError.value = '';
  if (!grantForm.value.tokens || grantForm.value.tokens <= 0) {
    formError.value = 'Số token cấp thêm phải lớn hơn 0.';
    return;
  }

  saving.value = true;
  try {
    const response = await http.post(ApiFactory.QUOTA.TENANT_GRANTS(grantsTenantId.value), {
      tokens: grantForm.value.tokens,
      expiresAtUtc: grantForm.value.expiresAt ? grantForm.value.expiresAt.toISOString() : null,
      reason: grantForm.value.reason.trim() || null
    });
    if (!response.ok) {
      formError.value = await readApiError(response, 'Không thể cấp thêm token');
      return;
    }
    grantForm.value = { tokens: null, expiresAt: null, reason: '' };
    toast.add({ severity: 'success', summary: 'Đã cấp thêm token', life: 3000 });
    await Promise.all([loadGrants(), reload()]);
  } catch (cause) {
    formError.value = errorMessage(cause, 'Không thể cấp thêm token.');
  } finally {
    saving.value = false;
  }
};

const confirmDeleteGrant = (grant: GrantRow) => {
  confirm.require({
    message: `Gỡ grant ${nf(grant.tokens)} token? Chỉ gỡ được grant chưa tiêu đồng nào.`,
    header: 'Gỡ grant',
    icon: 'pi pi-exclamation-triangle',
    acceptLabel: 'Gỡ',
    rejectLabel: 'Hủy',
    acceptProps: { severity: 'danger' },
    accept: async () => {
      try {
        const response = await http.delete(ApiFactory.QUOTA.GRANT_BY_ID(grant.id));
        if (!response.ok) {
          toast.add({ severity: 'error', summary: 'Không thể gỡ grant', detail: await readApiError(response, ''), life: 5000 });
          return;
        }
        toast.add({ severity: 'success', summary: 'Đã gỡ grant', life: 3000 });
        await Promise.all([loadGrants(), reload()]);
      } catch (cause) {
        toast.add({ severity: 'error', summary: 'Lỗi', detail: errorMessage(cause, 'Không thể gỡ grant.'), life: 5000 });
      }
    }
  });
};

onMounted(reload);
</script>
