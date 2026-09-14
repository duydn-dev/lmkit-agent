import { definePreset } from '@primeuix/themes';
import Aura from '@primeuix/themes/aura';

/**
 * Preset PrimeVue theo nhận diện khối Chính phủ Việt Nam cho CILA:
 * - Màu chủ đạo: đỏ quốc kỳ (họ #CE1126) làm primary — nút hành động chính,
 *   trạng thái active, focus ring.
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
    primary: {
      50: '{red.50}',
      100: '{red.100}',
      200: '{red.200}',
      300: '{red.300}',
      400: '{red.400}',
      500: '{red.600}',
      600: '{red.700}',
      700: '{red.800}',
      800: '{red.900}',
      900: '{red.950}',
      950: '{red.950}'
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
