<template>
  <div class="mermaid-diagram">
    <!-- Rendered SVG: mermaid output only, sanitised by securityLevel 'strict'. -->
    <div v-if="!error" class="mermaid-host" role="img" :aria-label="ariaLabel" v-html="svg"></div>

    <!-- Never a dead end: if the parser rejects the source, show the source itself. -->
    <div v-else class="rounded-lg border border-amber-200 bg-amber-50 p-3" role="alert">
      <p class="flex items-center gap-2 text-sm font-medium text-amber-800">
        <i class="pi pi-exclamation-triangle" aria-hidden="true"></i>
        Không dựng được hình từ mã sơ đồ
      </p>
      <p class="mt-1 text-xs text-amber-700">{{ error }}</p>
      <pre class="mt-2 max-h-56 overflow-auto rounded bg-white/70 p-2 text-[11px] leading-relaxed text-gray-700">{{ source }}</pre>
    </div>

    <div v-if="loading" class="flex items-center justify-center gap-2 p-6 text-sm text-gray-500">
      <i class="pi pi-spin pi-spinner" aria-hidden="true"></i> Đang dựng sơ đồ…
    </div>
  </div>
</template>

<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue';

const props = withDefaults(
  defineProps<{
    source: string;
    ariaLabel?: string;
  }>(),
  { ariaLabel: 'Sơ đồ quan hệ cơ sở dữ liệu' }
);

const emit = defineEmits<{ rendered: []; failed: [message: string] }>();

const svg = ref('');
const error = ref('');
const loading = ref(false);

// Mermaid is ~1MB; it is only loaded when a diagram is actually shown (admin-only screen).
let mermaidReady: Promise<typeof import('mermaid').default> | null = null;
let renderSeq = 0;
let disposed = false;

function loadMermaid() {
  mermaidReady ??= import('mermaid').then((module) => {
    const mermaid = module.default;
    mermaid.initialize({
      startOnLoad: false,
      // Labels come from database identifiers, i.e. third-party input: sanitise them.
      securityLevel: 'strict',
      theme: 'base',
      themeVariables: {
        fontFamily: "'Be Vietnam Pro', ui-sans-serif, system-ui, sans-serif",
        fontSize: '13px',
        primaryColor: '#ffffff',
        primaryTextColor: '#111827',
        primaryBorderColor: '#b81f33',
        lineColor: '#6b7280',
        tertiaryColor: '#f9fafb'
      },
      er: {
        useMaxWidth: false,
        layoutDirection: 'TB',
        minEntityWidth: 120,
        minEntityHeight: 40,
        entityPadding: 12,
        diagramPadding: 8,
        fontSize: 12
      }
    });
    return mermaid;
  });
  return mermaidReady;
}

async function render() {
  const source = props.source?.trim() ?? '';
  const token = ++renderSeq;

  if (source.length === 0) {
    svg.value = '';
    error.value = '';
    return;
  }

  loading.value = true;
  const id = `er-diagram-${token}-${Math.random().toString(36).slice(2, 8)}`;
  try {
    const mermaid = await loadMermaid();
    const result = await mermaid.render(id, source);
    // A newer render started while this one was in flight — drop the stale result.
    if (disposed || token !== renderSeq) return;
    svg.value = result.svg;
    error.value = '';
    await nextTick();
    emit('rendered');
  } catch (cause) {
    if (disposed || token !== renderSeq) return;
    svg.value = '';
    error.value = cause instanceof Error ? cause.message.split('\n')[0] : String(cause);
    emit('failed', error.value);
  } finally {
    loading.value = false;
    // Mermaid leaves its measuring node behind when parsing throws. Only that node is
    // removed: the SVG it returns is injected with the SAME id, so removing by `id` here
    // would delete the diagram we just rendered.
    document.getElementById(`d${id}`)?.remove();
  }
}

// immediate: the first render starts as soon as the source arrives — no second pass on mount.
watch(() => props.source, render, { immediate: true });
onBeforeUnmount(() => {
  disposed = true;
});
</script>

<style scoped>
.mermaid-host {
  overflow: auto;
  max-height: 62vh;
}
.mermaid-host :deep(svg) {
  display: block;
}
</style>
