import { createApp } from 'vue'
import { createPinia } from 'pinia'
import './style.css'
import App from './App.vue'
import PrimeVue from 'primevue/config'
import { GovPreset } from './theme/gov-preset'
import 'primeicons/primeicons.css'
// Font quốc ngữ chính thức của giao diện: Be Vietnam Pro (self-host qua
// @fontsource — không phụ thuộc CDN ngoài, đúng yêu cầu hạ tầng nội bộ).
import '@fontsource/be-vietnam-pro/400.css'
import '@fontsource/be-vietnam-pro/500.css'
import '@fontsource/be-vietnam-pro/600.css'
import '@fontsource/be-vietnam-pro/700.css'
import router from './router'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import Tooltip from 'primevue/tooltip'

const app = createApp(App)
const pinia = createPinia()

app.use(pinia)
app.use(router)
app.use(PrimeVue, {
    theme: {
        preset: GovPreset,
        options: {
            darkModeSelector: false,
            cssLayer: false
        }
    }
})
app.use(ToastService)
app.use(ConfirmationService)
app.directive('tooltip', Tooltip)
app.mount('#app')

// --- PWA service worker -----------------------------------------------------
// Chỉ đăng ký trên bản build production: dev server đổi module liên tục nên
// không được cache. `navigator.webdriver` loại trừ trình duyệt tự động hóa —
// e2e (Playwright) chạy trên bản preview production, và service worker chen
// vào giữa page.route sẽ khiến mock API không còn tất định.
// Thất bại phải im lặng: PWA là tăng cường, không phải tính năng lõi.
if (import.meta.env.PROD && 'serviceWorker' in navigator && !navigator.webdriver) {
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js').catch(() => {
      /* im lặng: ứng dụng hoạt động bình thường khi không có service worker */
    })
  })
}
