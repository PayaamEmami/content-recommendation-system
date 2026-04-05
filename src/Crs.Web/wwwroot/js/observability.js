(function () {
    const defaultRumConfig = {
        enabled: false,
        endpoint: "https://client.rum.us-east-1.amazonaws.com/1.0.2/cwr.js",
        telemetries: ["errors", "performance", "http"],
        sessionSampleRate: 0.1,
        allowCookies: true,
        enableXRay: true
    };

    async function readJson(path) {
        try {
            const response = await fetch(path, { cache: "no-store" });
            if (!response.ok) {
                return {};
            }

            return await response.json();
        } catch {
            return {};
        }
    }

    function mergeRum(baseConfig, overrideConfig) {
        return {
            ...baseConfig,
            ...overrideConfig,
            telemetries: Array.isArray(overrideConfig.telemetries) && overrideConfig.telemetries.length > 0
                ? overrideConfig.telemetries
                : baseConfig.telemetries
        };
    }

    function loadRumClient(scriptUrl, config) {
        return new Promise((resolve, reject) => {
            (function (n, i, v, r, s, c, x, z) {
                x = window.AwsRumClient = { q: [], n: n, i: i, v: v, r: r, c: c };
                window[n] = function (command, params) {
                    x.q.push({ c: command, p: params });
                };

                z = document.createElement("script");
                z.async = true;
                z.src = s;
                z.onload = resolve;
                z.onerror = reject;
                document.head.appendChild(z);
            })(
                "cwr",
                config.appMonitorId,
                "1.0.0",
                config.region,
                scriptUrl,
                {
                    allowCookies: config.allowCookies,
                    enableXRay: config.enableXRay,
                    endpoint: config.dataplaneEndpoint || ("https://dataplane.rum." + config.region + ".amazonaws.com"),
                    guestRoleArn: config.guestRoleArn,
                    identityPoolId: config.identityPoolId,
                    sessionSampleRate: config.sessionSampleRate,
                    telemetries: config.telemetries
                }
            );
        });
    }

    async function initializeRum() {
        const sharedConfig = await readJson("appsettings.json");
        const environmentConfig = await readJson("appsettings.Production.json");
        const rum = mergeRum(
            defaultRumConfig,
            mergeRum(
                sharedConfig?.Observability?.Rum ?? {},
                environmentConfig?.Observability?.Rum ?? {}));

        window.__crsObservability = {
            rum
        };

        if (!rum.enabled ||
            !rum.appMonitorId ||
            !rum.appMonitorName ||
            !rum.region ||
            !rum.identityPoolId) {
            return;
        }

        try {
            await loadRumClient(rum.endpoint, rum);
        } catch (error) {
            console.warn("CloudWatch RUM initialization failed", error);
        }
    }

    initializeRum();
})();
