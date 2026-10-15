import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createTestingPinia } from '@pinia/testing';
import { flushPromises, mount } from '@vue/test-utils';
import { reactive, ref } from 'vue';

const fixtures = vi.hoisted(() => ({ saved: {}, stop: vi.fn().mockResolvedValue(true) }));

vi.mock('../../../views/Feed/Feed.vue', () => ({
    default: { template: '<div />' }
}));
vi.mock('../../../views/Feed/columns.jsx', () => ({ columns: [] }));
vi.mock('../../../plugins/router', () => ({
    router: {
        beforeEach: vi.fn(),
        push: vi.fn(),
        replace: vi.fn(),
        currentRoute: ref({ path: '/login', name: 'login', meta: {} }),
        isReady: vi.fn().mockResolvedValue(true)
    },
    initRouter: vi.fn()
}));
vi.mock('vue-router', async (importOriginal) => {
    const actual = await importOriginal();
    return {
        ...actual,
        useRouter: vi.fn(() => ({
            push: vi.fn(),
            replace: vi.fn(),
            currentRoute: ref({ path: '/login', name: 'login', meta: {} })
        })),
        useRoute: vi.fn(() => ({ query: {} }))
    };
});
vi.mock('../../../plugins/interopApi', () => ({ initInteropApi: vi.fn() }));
vi.mock('../../../services/database', () => ({
    database: new Proxy(
        {},
        {
            get: (_target, prop) => {
                if (prop === '__esModule') return false;
                return vi.fn().mockResolvedValue(null);
            }
        }
    )
}));
vi.mock('../../../services/config', () => ({
    default: {
        init: vi.fn(),
        getString: vi
            .fn()
            .mockImplementation((key, d) =>
                key === 'savedCredentials' ? JSON.stringify(fixtures.saved) : (d ?? '{}')
            ),
        setString: vi.fn(),
        getBool: vi.fn().mockImplementation((_k, d) => d ?? false),
        setBool: vi.fn(),
        getInt: vi.fn().mockImplementation((_k, d) => d ?? 0),
        setInt: vi.fn(),
        getFloat: vi.fn().mockImplementation((_k, d) => d ?? 0),
        setFloat: vi.fn(),
        getObject: vi.fn().mockReturnValue(null),
        setObject: vi.fn(),
        getArray: vi.fn().mockReturnValue([]),
        setArray: vi.fn(),
        remove: vi.fn()
    }
}));
vi.mock('../../../services/jsonStorage', () => ({ default: vi.fn() }));
vi.mock('../../../services/watchState', () => ({
    watchState: {
        isLoggedIn: false,
        isFriendsLoaded: false,
        isFavoritesLoaded: false
    }
}));
vi.mock('vee-validate', () => ({
    Field: {
        name: 'VeeField',
        props: ['name'],
        setup(_props, { slots }) {
            return () =>
                slots.default?.({
                    field: { value: '', onChange: () => {}, onBlur: () => {} },
                    errors: []
                });
        }
    },
    useForm: vi.fn(() => ({
        handleSubmit: (fn) => fn,
        resetForm: vi.fn(),
        values: reactive({})
    }))
}));
vi.mock('@vee-validate/zod', () => ({ toTypedSchema: vi.fn((s) => s) }));

import Login from '../Login.vue';
import en from '../../../localization/en.json';
import configRepository from '../../../services/config';

const i18n = createI18n({
    locale: 'en',
    fallbackLocale: 'en',
    legacy: false,
    globalInjection: false,
    missingWarn: false,
    fallbackWarn: false,
    messages: { en }
});

const stubs = {
    QrLogin: {
        template: '<section class="qr-login-stub" />',
        setup(_, { expose }) {
            expose({ stop: fixtures.stop });
        }
    },
    LoginSettingsDialog: { template: '<div class="login-settings-stub" />' },
    TooltipWrapper: {
        template: '<span><slot /></span>',
        props: ['side', 'content']
    },
    DropdownMenu: { template: '<div class="dropdown-stub"><slot /></div>' },
    DropdownMenuTrigger: {
        template: '<span><slot /></span>',
        props: ['asChild']
    },
    DropdownMenuContent: { template: '<div><slot /></div>' },
    DropdownMenuCheckboxItem: {
        template: '<div><slot /></div>',
        props: ['modelValue']
    },
    Button: {
        template: '<button :type="type || \'button\'" :id="id"><slot /></button>',
        props: ['type', 'variant', 'size', 'id']
    },
    Checkbox: { template: '<input type="checkbox" />', props: ['modelValue'] },
    Field: { template: '<div><slot /></div>' },
    FieldContent: { template: '<div><slot /></div>' },
    FieldError: { template: '<span />', props: ['errors'] },
    FieldGroup: { template: '<div><slot /></div>' },
    FieldLabel: { template: '<label><slot /></label>', props: ['for'] },
    InputGroupField: {
        template: '<input :id="id" :value="modelValue" :placeholder="placeholder" />',
        props: ['id', 'modelValue', 'type', 'autocomplete', 'name', 'placeholder', 'ariaInvalid', 'clearable'],
        emits: ['update:modelValue', 'blur']
    },
    ArrowBigDownDash: { template: '<span />' },
    Languages: { template: '<span />' },
    Trash2: { template: '<span />' }
};

/**
 * @param storeOverrides
 */
const wrappers = [];
function mountLogin(storeOverrides = {}) {
    const pinia = createTestingPinia({
        stubActions: false,
        initialState: {
            Auth: {
                loginForm: {
                    loading: false,
                    username: '',
                    password: '',
                    endpoint: '',
                    websocket: '',
                    saveCredentials: false,
                    lastUserLoggedIn: ''
                },
                ...storeOverrides
            }
        }
    });
    const wrapper = mount(Login, {
        global: {
            plugins: [i18n, pinia],
            stubs
        }
    });
    wrappers.push(wrapper);
    return wrapper;
}

describe('Login.vue', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        fixtures.saved = {};
        configRepository.setString.mockImplementation((key, value) => {
            if (key === 'savedCredentials') fixtures.saved = JSON.parse(value);
        });
    });
    afterEach(() => {
        wrappers.splice(0).forEach((wrapper) => wrapper.unmount());
    });

    describe('form rendering', () => {
        test('renders username and password input fields', () => {
            const wrapper = mountLogin();
            const usernameInput = wrapper.find('#login-form-username');
            const passwordInput = wrapper.find('#login-form-password');
            expect(usernameInput.exists()).toBe(true);
            expect(passwordInput.exists()).toBe(true);
        });

        test('renders a login submit button', () => {
            const wrapper = mountLogin();
            const form = wrapper.find('#login-form');
            expect(form.exists()).toBe(true);
            const submitBtn = form.find('button[type="submit"]');
            expect(submitBtn.exists()).toBe(true);
        });

        test('renders a register button', () => {
            const wrapper = mountLogin();
            const buttons = wrapper.findAll('button');
            const registerBtn = buttons.find((b) => b.text() === en.view.login.register);
            expect(registerBtn).toBeTruthy();
        });

        test('renders save credentials checkbox', () => {
            const wrapper = mountLogin();
            const checkbox = wrapper.find('input[type="checkbox"]');
            expect(checkbox.exists()).toBe(true);
        });
    });

    describe('saved accounts section', () => {
        test('automatically shows the QR panel after deleting the last saved account', async () => {
            fixtures.saved = { usr_one: { user: { id: 'usr_one', displayName: 'Only account' }, loginParams: {} } };
            const wrapper = mountLogin();
            await flushPromises();
            expect(wrapper.find('.qr-login-stub').exists()).toBe(false);
            await wrapper.find('.x-saved-account-list button').trigger('click');
            await flushPromises();
            expect(wrapper.find('.qr-login-stub').exists()).toBe(true);
            expect(wrapper.text()).not.toContain(en.view.login.qr.add_account);
        });
        test('always displays QR login to the right of the password form without saved accounts', async () => {
            const wrapper = mountLogin();
            await flushPromises();
            const divider = wrapper.find('.x-vertical-divider');
            expect(divider.exists()).toBe(true);
            expect(wrapper.find('.qr-login-stub').exists()).toBe(true);
            expect(wrapper.find('#login-form').exists()).toBe(true);
            expect(
                wrapper
                    .find('.x-login-form-container')
                    .element.children[2].contains(wrapper.find('.qr-login-stub').element)
            ).toBe(true);
            expect(wrapper.text()).not.toContain(en.view.login.qr.add_account);
        });

        test('places QR add button below the last saved account and opens and closes the QR panel', async () => {
            fixtures.saved = {
                usr_one: { user: { id: 'usr_one', displayName: 'First account', username: 'first' }, loginParams: {} },
                usr_two: { user: { id: 'usr_two', displayName: 'Last account', username: 'last' }, loginParams: {} }
            };
            const wrapper = mountLogin();
            await flushPromises();
            expect(wrapper.find('.qr-login-stub').exists()).toBe(false);
            const button = wrapper.findAll('button').find((button) => button.text() === en.view.login.qr.add_account);
            expect(button.element).toBe(wrapper.find('.x-saved-account-list').element.lastElementChild);
            expect(wrapper.text()).toContain('Last account');
            await button.trigger('click');
            expect(wrapper.find('.qr-login-stub').exists()).toBe(true);
            expect(wrapper.find('#login-form').exists()).toBe(true);
            await wrapper
                .findAll('button')
                .find((button) => button.text() === en.view.login.qr.back)
                .trigger('click');
            expect(wrapper.find('.qr-login-stub').exists()).toBe(false);
            expect(wrapper.text()).toContain('Last account');
        });
    });

    describe('legal notice', () => {
        test('renders legal notice section', () => {
            const wrapper = mountLogin();
            const legalNotice = wrapper.find('.x-legal-notice-container');
            expect(legalNotice.exists()).toBe(true);
        });
    });

    describe('login settings', () => {
        test('renders LoginSettingsDialog stub', () => {
            const wrapper = mountLogin();
            expect(wrapper.find('.login-settings-stub').exists()).toBe(true);
        });
    });
});
