import { definePreset } from '@primeuix/themes';
import Aura from '@primeuix/themes/aura';

/**
 * Preset PrimeVue theo nhận diện khối Chính phủ Việt Nam cho CILA:
 * - Màu chủ đạo (primary): XANH DƯƠNG đậm (#2563eb, hover #1d4ed8) — nút hành
 *   động chính, trạng thái active, focus ring. Đúng quy ước PrimeNG: primary
 *   là màu hành động; ĐỎ chỉ dành cho severity="danger" (xóa, dừng, từ chối,
 *   thu hồi, lỗi) — để đỏ ở primary khiến mọi nút tạo mới nhìn như báo lỗi.
 * - Thang đỏ quốc kỳ (primitive.red) vẫn giữ để danger dùng đúng đỏ cờ.
 * - Bo góc kín đáo (6px) và focus ring rõ (WCAG/Thông tư 22/2023/TT-BTTTT yêu
 *   cầu tiếp cận được bằng bàn phím).
 * - Giao diện một tông sáng (darkModeSelector đã tắt trong main.ts) — cổng
 *   thông tin cơ quan nhà nước dùng nền sáng, chữ đậm, tương phản cao.
 */
export const GovPreset = definePreset(Aura, {
  primitive: {
    borderRadius: {
      none: '0',
      xs: '2px',
      sm: '4px',
      md: '6px',
      lg: '8px',
      xl: '10px'
    },
    // Thang đỏ quốc kỳ: 600 là màu làm việc chính (#b81f33 — đỏ cờ hạ nhẹ độ
    // chói để đạt tương phản AA trên nền trắng), 500 giữ tông cờ #CE1126.
    red: {
      50: '#fdf2f3',
      100: '#fbe3e5',
      200: '#f6bcc1',
      300: '#ee8f98',
      400: '#e35a68',
      500: '#ce1126',
      600: '#b81f33',
      700: '#9a1a2c',
      800: '#7f1826',
      900: '#6a1a25',
      950: '#3c0b12'
    }
  },
  semantic: {
    // Font toàn cục cho mọi component PrimeVue: cùng Be Vietnam Pro với app
    // (thay vì Inter của Aura mặc định) — hết cảnh dialog một font, app một font.
    typography: {
      fontFamily: "'Be Vietnam Pro', ui-sans-serif, system-ui, sans-serif"
    },
    // Thang xanh dương chính phủ, neo vào xanh chrome của app (--color-gov-blue-dark
    // = #1e3a8a nằm ở bậc 800). Nút mặc định #2563eb, hover #1d4ed8 — tương trắng AA.
    primary: {
      50: '#eff6ff',
      100: '#dbeafe',
      200: '#bfdbfe',
      300: '#93c5fd',
      400: '#60a5fa',
      500: '#2563eb',
      600: '#1d4ed8',
      700: '#1e40af',
      800: '#1e3a8a',
      900: '#172554',
      950: '#0f1c3f'
    },
    focusRing: {
      width: '2px',
      style: 'solid',
      color: '{primary.500}',
      offset: '2px'
    },
    colorScheme: {
      light: {
        surface: {
          0: '#ffffff',
          50: '#f8fafc',
          100: '#f1f5f9',
          200: '#e2e8f0',
          300: '#cbd5e1',
          400: '#94a3b8',
          500: '#64748b',
          600: '#475569',
          700: '#334155',
          800: '#1e293b',
          900: '#0f172a',
          950: '#020617'
        }
      }
    }
  }
});
