<template>
  <div class="generative-ui-renderer">
    <!-- Render text part -->
    <div class="text-base font-medium leading-relaxed whitespace-pre-wrap break-words text-gray-800 markdown-body" v-html="formatMessage(textContent)"></div>
    
    <!-- Render charts if found -->
    <div v-for="(chart, index) in charts" :key="index" class="mt-4 p-4 bg-white rounded-xl shadow-sm border border-gray-100 overflow-hidden">
      <h4 v-if="chart.title" class="text-lg font-semibold mb-4 text-center text-gray-700">{{ chart.title }}</h4>
      <Chart :type="chart.type || 'bar'" :data="chart.data" :options="chart.options || defaultOptions" class="w-full h-[300px]" />
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';

const props = defineProps<{
  content: string;
}>();

const formatMessage = (text: string) => {
  return text.replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>').replace(/\n/g, '<br/>');
};

const defaultOptions = {
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: {
      position: 'bottom'
    }
  }
};

const parsedContent = computed(() => {
  let textContent = props.content;
  const charts = [];
  
  // Extract all <chart>...</chart> blocks
  const chartRegex = /<chart>([\s\S]*?)<\/chart>/g;
  let match;
  
  while ((match = chartRegex.exec(props.content)) !== null) {
    try {
      const jsonStr = match[1];
      const data = JSON.parse(jsonStr);
      charts.push(data);
    } catch (e) {
      console.error("Failed to parse chart JSON:", e);
    }
  }
  
  // Remove chart blocks from text
  textContent = textContent.replace(chartRegex, '').trim();
  
  return {
    textContent,
    charts
  };
});

const textContent = computed(() => parsedContent.value.textContent);
const charts = computed(() => parsedContent.value.charts);
</script>

<style scoped>
.generative-ui-renderer {
  width: 100%;
}
</style>
