import { createApp } from 'vue'
import { createPinia } from 'pinia'
import './style.css'
import App from './App.vue'
import PrimeVue from 'primevue/config'
import Aura from '@primeuix/themes/aura'
import 'primeicons/primeicons.css'
import router from './router'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'

const app = createApp(App)
const pinia = createPinia()

app.use(pinia)
app.use(router)
app.use(PrimeVue, {
    theme: {
        preset: Aura,
        options: {
            darkModeSelector: 'system',
            cssLayer: false
        }
    }
})
app.use(ToastService)
app.use(ConfirmationService)
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
