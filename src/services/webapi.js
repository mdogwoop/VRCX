// requires binding of WebApi

class WebApiService {
    async beginQrLogin() {
        return JSON.parse(await WebApi.BeginQrLogin());
    }

    async getQrLoginState(operationId) {
        return JSON.parse(await WebApi.GetQrLoginState(operationId));
    }

    cancelQrLogin() {
        return WebApi.CancelQrLogin();
    }

    async commitQrLogin(operationId) {
        return JSON.parse(await WebApi.CommitQrLogin(operationId));
    }

    saveExternalSession(userId) {
        return LINUX ? window.electron.saveAuthSession(userId) : WebApi.SaveExternalSession(userId);
    }

    async loadExternalSession(userId) {
        const result = LINUX ? await window.electron.loadAuthSession(userId) : await WebApi.LoadExternalSession(userId);
        return JSON.parse(result);
    }

    deleteExternalSession(userId) {
        return LINUX ? window.electron.deleteAuthSession(userId) : WebApi.DeleteExternalSession(userId);
    }

    clearCookies() {
        return WebApi.ClearCookies();
    }

    getCookies() {
        return WebApi.GetCookies();
    }

    setCookies(cookie) {
        return WebApi.SetCookies(cookie);
    }

    /**
     * @param {import('@/types/vrcx').WebApiOptions} options
     * @returns {Promise<{ status: number; data?: string }>}
     */
    async execute(options) {
        if (!options) {
            throw new Error('options is required');
        }
        if (LINUX) {
            const requestJson = JSON.stringify(options);
            var json = await WebApi.ExecuteJson(requestJson);
            var data = JSON.parse(json);
            if (data.status === -1) {
                throw new Error(data.message);
            }
            return {
                status: data.status,
                data: data.message
            };
        }

        var item = await WebApi.Execute(options);
        if (item.Item1 === -1) {
            throw item.Item2;
        }
        return {
            status: item.Item1,
            data: item.Item2
        };
    }
}

var self = new WebApiService();
window.webApiService = self;

export { self as default, WebApiService };
