// Run with Playwright browser_run_code_unsafe(filename=absolute path) on the Web page.
// All API responses are synthetic; this check never writes service settings.
async (page) => {
  const origin = new URL(page.url()).origin;
  const reports = [];
  for (const mode of ["failed", "delayed"]) {
    let release;
    const waiting = new Promise(resolve => { release = resolve; });
    let submitted;
    const state = {
      settings: { refreshHours: 6, repoCount: 10, inactiveDays: 7, autoRefresh: false,
        analysisConcurrency: 1, skipSearched: false, skipFavorites: false,
        searchHistoryDays: 1, logRetentionDays: 7, downloadMirror: "ghfast.top" },
      attempt: { running: false, phase: "idle", completed: 0, total: 0 },
      repositories: [], favorites: [], history: [], logs: [], generatedAt: null,
      nextRunAt: null, tokenConfigured: false, feedPath: "/subscriptions.txt",
      checker: { configured: false, webUrl: null },
    };
    const routeApi = async route => {
      const request = route.request();
      const path = new URL(request.url()).pathname;
      if (path === "/api/session") return route.fulfill({ json: { authenticated: true, csrfToken: "synthetic-csrf" } });
      if (path === "/api/state") return route.fulfill({ json: state });
      if (path === "/api/connections") return route.fulfill({ json: {
        github: { source: "environment", configured: false },
        checker: { source: "environment", configured: false, keyConfigured: false },
      } });
      if (path === "/api/mirrors") {
        if (mode === "delayed") {
          await waiting;
          return route.fulfill({ json: [{ name: "ghfast.top", prefix: "https://ghfast.top/", isDefault: false }] });
        }
        return route.fulfill({ status: 503, json: { title: "Synthetic mirror catalog failure" } });
      }
      if (path === "/api/settings" && request.method() === "PUT") {
        submitted = request.postDataJSON();
        state.settings = submitted;
        return route.fulfill({ status: 204 });
      }
      throw new Error(`Unexpected synthetic request: ${path}`);
    };
    const pattern = `${origin}/api/**`;
    await page.route(pattern, routeApi);
    try {
      await page.goto(`${origin}/?browser-settings-check=${mode}#settings`);
      await page.reload();
      await page.waitForFunction(() => document.getElementById("refresh-hours").value === "6");
      await page.locator("#refresh-hours").fill("7");
      if (mode === "delayed") {
        const response = page.waitForResponse(`${origin}/api/mirrors`);
        release();
        await response;
      } else {
        await page.waitForFunction(() => document.getElementById("mirror-message").textContent.includes("failure"));
      }
      await page.locator("#settings-save").click();
      await page.waitForFunction(() => document.getElementById("settings-message").textContent.includes("设置已保存"));
      if (submitted?.downloadMirror !== "ghfast.top" || submitted.refreshHours !== 7)
        throw new Error(`${mode} mirror catalog changed an unedited mirror setting`);
      reports.push(`${mode}: existing mirror retained when only refreshHours changed`);
    } finally {
      release();
      await page.unroute(pattern, routeApi);
    }
  }
  return reports;
}
