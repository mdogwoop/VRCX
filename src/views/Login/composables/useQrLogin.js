import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { clearTimeout, setTimeout } from 'worker-timers';
import QRCode from 'qrcode';

import webApiService from '../../../services/webapi';

// Serialize backend mutations across panel mounts. A cancelled request must finish
// before another panel can create a new operation in the single backend slot.
let backendQueue = Promise.resolve();
/** @template T @param {() => T | Promise<T>} action @returns {Promise<T>} */
function mutateBackend(action) {
    const next = backendQueue.then(action, action);
    backendQueue = next.then(
        () => {},
        () => {}
    );
    return next;
}

export function useQrLogin({ paused, disabled, prepare, complete, remember, setLoading }) {
    const status = ref('idle');
    const error = ref('');
    const image = ref('');
    const committing = computed(() => status.value === 'committing');
    let generation = 0;
    let operationId;
    let timer;
    let disposed = false;
    let finished = false;

    async function stop() {
        if (committing.value) return false;
        generation++;
        clearTimeout(timer);
        image.value = '';
        operationId = undefined;
        status.value = 'idle';
        await mutateBackend(() => webApiService.cancelQrLogin()).catch(() => {});
        return true;
    }

    function fail(code) {
        image.value = '';
        status.value = 'failed';
        error.value = code || 'network_error';
        void mutateBackend(() => webApiService.cancelQrLogin()).catch(() => {});
    }

    async function accept(state, current) {
        if (disposed || current !== generation) return;
        if (state.status === 'ready') {
            if (paused() || disabled()) return;
            status.value = 'committing';
            image.value = '';
            setLoading(true);
            try {
                const user = await mutateBackend(() => webApiService.commitQrLogin(operationId));
                // Read the shared checkbox at approval time, not when the QR was created.
                await complete(user, remember());
                finished = true;
                status.value = 'completed';
            } catch {
                fail('login_failed');
            } finally {
                setLoading(false);
            }
            return;
        }
        if (state.status === 'expired' || state.error === 'expired') {
            await start();
            return;
        }
        if (!['waiting', 'validating'].includes(state.status)) {
            fail(state.error || 'unavailable');
            return;
        }
        if (state.verificationUrl && !image.value) {
            const url = new URL(state.verificationUrl);
            if (url.protocol !== 'https:' || url.hostname !== 'vrchat.com' || url.pathname !== '/home/device') {
                fail('unavailable');
                return;
            }
            const data = await QRCode.toDataURL(state.verificationUrl, {
                width: 208,
                margin: 3,
                errorCorrectionLevel: 'M',
                color: { dark: '#000000', light: '#ffffff' }
            });
            if (disposed || current !== generation) return;
            image.value = data;
        }
        status.value = state.status;
        timer = setTimeout(async () => {
            try {
                await accept(await webApiService.getQrLoginState(operationId), current);
            } catch {
                if (!disposed && current === generation) fail('network_error');
            }
        }, 1000);
    }

    async function start() {
        if (disposed || finished || committing.value || paused() || disabled()) return;
        const current = ++generation;
        clearTimeout(timer);
        image.value = '';
        error.value = '';
        status.value = 'starting';
        try {
            const state = await mutateBackend(async () => {
                await webApiService.cancelQrLogin();
                if (disposed || current !== generation) return;
                await prepare();
                if (disposed || current !== generation || paused() || disabled()) return;
                return webApiService.beginQrLogin();
            });
            if (!state || disposed || current !== generation) return;
            operationId = state.operationId;
            await accept(state, current);
        } catch {
            if (!disposed && current === generation) fail('network_error');
        }
    }

    watch(
        [paused, disabled],
        ([isPaused, isDisabled]) => {
            if (committing.value) return;
            if (isPaused || isDisabled) void stop();
            else if (status.value === 'idle') void start();
        },
        { immediate: true }
    );

    onBeforeUnmount(() => {
        disposed = true;
        generation++;
        clearTimeout(timer);
        void mutateBackend(() => webApiService.cancelQrLogin()).catch(() => {});
    });

    return { status, error, image, committing, start, stop };
}
