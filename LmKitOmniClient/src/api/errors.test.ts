import { describe, expect, it } from 'vitest';
import { errorMessage, readApiError } from './errors';

describe('API error contract', () => {
  it('prefers the explicit API message', async () => {
    const response = Response.json({ message: 'Thông tin không hợp lệ.' }, { status: 400 });
    await expect(readApiError(response, 'Yêu cầu thất bại')).resolves.toBe('Thông tin không hợp lệ.');
  });

  it('extracts the first validation problem detail', async () => {
    const response = Response.json({
      title: 'Validation failed',
      errors: { Password: ['Mật khẩu phải có ít nhất 12 ký tự.'] }
    }, { status: 400 });
    await expect(readApiError(response, 'Yêu cầu thất bại')).resolves.toBe('Mật khẩu phải có ít nhất 12 ký tự.');
  });

  it('does not render an HTML proxy error page', async () => {
    const response = new Response('<html><body>gateway error</body></html>', {
      status: 502,
      headers: { 'content-type': 'text/html' }
    });
    await expect(readApiError(response, 'Máy chủ không phản hồi')).resolves.toBe('Máy chủ không phản hồi (502).');
  });

  it('normalizes thrown errors and unknown failures', () => {
    expect(errorMessage(new Error('Mất kết nối.'), 'Thất bại.')).toBe('Mất kết nối.');
    expect(errorMessage('opaque', 'Thất bại.')).toBe('Thất bại.');
  });
});
