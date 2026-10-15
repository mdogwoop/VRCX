<template>
    <section class="x-qr-login" :aria-label="t('view.login.qr.title')">
        <h2 class="font-bold text-center m-0">{{ t('view.login.qr.title') }}</h2>
        <div class="x-qr-code" :aria-busy="status === 'starting' || committing">
            <img v-if="image" :src="image" :alt="t('view.login.qr.image_alt')" width="208" height="208" />
            <LoaderCircle
                v-else-if="!enableCustomEndpoint && status !== 'failed'"
                class="size-8 animate-spin text-muted-foreground"
                aria-hidden="true" />
            <QrCode v-else class="size-12 text-muted-foreground" aria-hidden="true" />
        </div>
        <p class="text-sm text-center text-muted-foreground m-0" role="status">
            {{
                enableCustomEndpoint
                    ? t('view.login.qr.errors.custom_endpoint')
                    : status === 'failed'
                      ? t(`view.login.qr.errors.${error}`)
                      : committing
                        ? t('view.login.qr.signing_in')
                        : paused
                          ? t('view.login.qr.paused')
                          : t('view.login.qr.instruction')
            }}
        </p>
        <p
            v-if="loginForm.saveCredentials && enablePrimaryPassword"
            class="text-xs text-center text-muted-foreground m-0">
            {{ t('view.login.qr.primary_password_hint') }}
        </p>
        <Button v-if="status === 'failed' && !enableCustomEndpoint" variant="outline" :disabled="paused" @click="start">
            <RefreshCw class="size-4" />{{ t('view.login.qr.retry') }}
        </Button>
    </section>
</template>

<script setup>
    import { computed } from 'vue';
    import { storeToRefs } from 'pinia';
    import { useI18n } from 'vue-i18n';
    import { LoaderCircle, QrCode, RefreshCw } from 'lucide-vue-next';
    import { Button } from '@/components/ui/button';
    import { useAuthStore } from '../../stores/auth';
    import { useAdvancedSettingsStore } from '../../stores/settings/advanced';
    import { useQrLogin } from './composables/useQrLogin';

    const props = defineProps({ paused: Boolean });
    const { t } = useI18n();
    const authStore = useAuthStore();
    const { loginForm, enableCustomEndpoint } = storeToRefs(authStore);
    const { enablePrimaryPassword } = storeToRefs(useAdvancedSettingsStore());
    const paused = computed(() => props.paused || loginForm.value.loading);
    const { status, error, image, committing, start, stop } = useQrLogin({
        paused: () => paused.value,
        disabled: () => enableCustomEndpoint.value,
        prepare: () => authStore.prepareQrLogin(),
        complete: (user, remember) => authStore.completeQrLogin(user, remember),
        remember: () => loginForm.value.saveCredentials,
        setLoading: (value) => {
            loginForm.value.loading = value;
        }
    });
    defineExpose({ stop });
</script>

<style scoped>
    .x-qr-login {
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 12px;
    }
    .x-qr-code {
        display: flex;
        align-items: center;
        justify-content: center;
        width: 208px;
        height: 208px;
        flex-shrink: 0;
        border-radius: 8px;
        overflow: hidden;
        background: var(--muted);
    }
</style>
