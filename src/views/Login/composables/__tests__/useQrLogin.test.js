import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { defineComponent, ref } from 'vue';

const mocks = vi.hoisted(() => ({
    beginQrLogin: vi.fn(),
    getQrLoginState: vi.fn(),
    cancelQrLogin: vi.fn(),
    commitQrLogin: vi.fn(),
    toDataURL: vi.fn()
}));
vi.mock('../../../../services/webapi', () => ({ default: mocks }));
vi.mock('qrcode', () => ({ default: { toDataURL: mocks.toDataURL } }));
vi.mock('worker-timers', () => ({
    setTimeout: (fn, delay) => globalThis.setTimeout(fn, delay),
    clearTimeout: (timer) => globalThis.clearTimeout(timer)
}));
import { useQrLogin } from '../useQrLogin';

const waiting = { operationId: 'qr-1', status: 'waiting', verificationUrl: 'https://vrchat.com/home/device?code=MOCK' };
let wrapper;
function setup() {
    const paused = ref(false),
        disabled = ref(false),
        remember = ref(false);
    const complete = vi.fn().mockResolvedValue(undefined),
        prepare = vi.fn().mockResolvedValue(undefined);
    const setLoading = vi.fn((value) => {
        paused.value = value;
    });
    let login;
    wrapper = mount(
        defineComponent({
            setup() {
                login = useQrLogin({
                    paused: () => paused.value,
                    disabled: () => disabled.value,
                    remember: () => remember.value,
                    prepare,
                    complete,
                    setLoading
                });
                return () => null;
            }
        })
    );
    return { login, paused, disabled, remember, complete, prepare, setLoading };
}
describe('QR login lifecycle', () => {
    beforeEach(() => {
        vi.useFakeTimers();
        vi.resetAllMocks();
        mocks.beginQrLogin.mockResolvedValue({ ...waiting });
        mocks.getQrLoginState.mockResolvedValue({ ...waiting });
        mocks.toDataURL.mockResolvedValue('data:image/png;base64,mock');
        mocks.commitQrLogin.mockResolvedValue({ id: 'usr_mock' });
    });
    afterEach(async () => {
        wrapper?.unmount();
        await flushPromises();
        vi.useRealTimers();
    });

    test('automatically renders a QR code without blocking password login', async () => {
        const { login, setLoading } = setup();
        await flushPromises();
        expect(login.image.value).toContain('data:image/png');
        expect(login.status.value).toBe('waiting');
        expect(setLoading).not.toHaveBeenCalled();
    });
    test('uses the latest save checkbox and signs in directly after approval', async () => {
        const { login, remember, complete, setLoading } = setup();
        await flushPromises();
        remember.value = true;
        mocks.getQrLoginState.mockResolvedValueOnce({ status: 'ready' });
        await vi.advanceTimersByTimeAsync(1000);
        await flushPromises();
        expect(mocks.commitQrLogin).toHaveBeenCalledExactlyOnceWith('qr-1');
        expect(complete).toHaveBeenCalledWith({ id: 'usr_mock' }, true);
        expect(setLoading.mock.calls).toEqual([[true], [false]]);
        expect(login.status.value).toBe('completed');
        expect(mocks.beginQrLogin).toHaveBeenCalledOnce();
    });
    test('pauses for password login, ignores its late QR approval, then resumes after failure', async () => {
        const { login, paused, complete } = setup();
        await flushPromises();
        let resolve;
        mocks.getQrLoginState.mockReturnValueOnce(
            new Promise((done) => {
                resolve = done;
            })
        );
        await vi.advanceTimersByTimeAsync(1000);
        paused.value = true;
        await flushPromises();
        resolve({ status: 'ready' });
        await flushPromises();
        expect(complete).not.toHaveBeenCalled();
        expect(login.image.value).toBe('');
        paused.value = false;
        await flushPromises();
        expect(mocks.beginQrLogin).toHaveBeenCalledTimes(2);
        expect(login.status.value).toBe('waiting');
    });
    test('refreshes an expired QR code but stops on rate limits until an explicit retry', async () => {
        const { login } = setup();
        await flushPromises();
        mocks.getQrLoginState.mockResolvedValueOnce({ status: 'expired' });
        await vi.advanceTimersByTimeAsync(1000);
        await flushPromises();
        expect(mocks.beginQrLogin).toHaveBeenCalledTimes(2);
        mocks.getQrLoginState.mockResolvedValueOnce({ status: 'failed', error: 'rate_limited' });
        await vi.advanceTimersByTimeAsync(1000);
        await flushPromises();
        expect(login.error.value).toBe('rate_limited');
        await vi.advanceTimersByTimeAsync(60000);
        expect(mocks.beginQrLogin).toHaveBeenCalledTimes(2);
        await login.start();
        expect(mocks.beginQrLogin).toHaveBeenCalledTimes(3);
    });
    test('cancels a pending creation before any replacement operation is started', async () => {
        let resolve;
        mocks.beginQrLogin.mockReturnValueOnce(
            new Promise((done) => {
                resolve = done;
            })
        );
        const { login } = setup();
        await flushPromises();
        const stopped = login.stop();
        const started = login.start();
        resolve({ ...waiting });
        await stopped;
        await started;
        expect(mocks.beginQrLogin).toHaveBeenCalledTimes(2);
        expect(mocks.cancelQrLogin.mock.invocationCallOrder[1]).toBeLessThan(
            mocks.beginQrLogin.mock.invocationCallOrder[1]
        );
        expect(login.status.value).toBe('waiting');
    });
    test('does not restart automatically after an activation failure', async () => {
        const { login } = setup();
        await flushPromises();
        mocks.commitQrLogin.mockRejectedValueOnce(new Error('failed'));
        mocks.getQrLoginState.mockResolvedValueOnce({ status: 'ready' });
        await vi.advanceTimersByTimeAsync(1000);
        await flushPromises();
        await vi.advanceTimersByTimeAsync(60000);
        expect(login.status.value).toBe('failed');
        expect(mocks.beginQrLogin).toHaveBeenCalledOnce();
    });
    test('does not contact the device API for a custom endpoint', async () => {
        const { disabled, login } = setup();
        disabled.value = true;
        await flushPromises();
        expect(mocks.beginQrLogin).not.toHaveBeenCalled();
        expect(login.image.value).toBe('');
    });
});
