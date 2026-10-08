// Run via Playwright browser_run_code_unsafe(filename=...) on the local assets server.
// All API traffic is intercepted with synthetic data; no live service writes.
async (page) => {
  const origin = new URL(page.url()).origin;
  const reports = [];
  const writes = [];
  const failures = [];
  let authenticated = true;
  let expire = false;
  const repo = (fullName, score) => ({ fullName, score, totalNodes: 10, stars: 3, commitsLast7Days: 7,
    ageDays: 100, daysInactive: 1, lastPush: "2026-10-06T00:00:00Z", distinctActiveDays: 3,
    url: `https://github.com/${fullName}`, links: [{ url: `https://raw.githubusercontent.com/${fullName}/main/sub.txt`, name: "公开来源", isValid: true, isAnalyzed: true, nodeCount: 10 }] });
  const state = {
    settings: { refreshHours: 6, repoCount: 10, inactiveDays: 7, autoRefresh: false,
      analysisConcurrency: 1, skipSearched: false, skipFavorites: false,
      searchHistoryDays: 1, logRetentionDays: 7, downloadMirror: "ghfast.top" },
    attempt: { running: false, phase: "idle", completed: 0, total: 0 },
    repositories: [repo("alpha/source", 90), repo("beta/source", 40)], favorites: [], history: [], logs: [],
    generatedAt: null, nextRunAt: null, tokenConfigured: false, feedPath: "/subscriptions.txt",
    checker: { configured: true, webUrl: null },
  };
  const routeApi = async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === "/api/session") return route.fulfill({ json: { authenticated, csrfToken: "synthetic-csrf" } });
    if (path === "/api/state") return route.fulfill(expire ? { status: 401 } : { json: state });
    if (request.method() !== "GET") {
      writes.push({ path, body: request.postData() ? request.postDataJSON() : null });
      if (path === "/api/settings") state.settings = request.postDataJSON();
      return route.fulfill({ status: 204 });
    }
    const replies = {
      "/api/connections": { github: { source: "environment", configured: false }, checker: { source: "environment", configured: true, keyConfigured: true, apiUrl: "http://checker.test:8199" } },
      "/api/mirrors": [{ name: "ghfast.top", prefix: "https://ghfast.top/", isDefault: false }],
      "/api/checker/status": { checking: false },
      "/api/checker/results": { nodes: [], speedTest: false, mediaCheck: false },
      "/api/checker/config": { revision: "synthetic-revision", sources: { localCount: 2, remoteCount: 0 }, fields: [
        { key: "speed-test", label: "启用测速", kind: "boolean", value: false },
        { key: "save-method", label: "保存方式", kind: "choice", value: "local", choices: ["local", "gist"] },
      ] },
      "/api/search-history": [],
    };
    if (Object.hasOwn(replies, path)) return route.fulfill({ json: replies[path] });
    failures.push(`Unexpected API: ${path}`);
    return route.fulfill({ status: 500 });
  };
  const onError = error => failures.push(error.message);
  const onDialog = async dialog => { failures.push(`Native dialog: ${dialog.type()}`); await dialog.dismiss(); };
  const assert = (value, message) => { if (!value) throw new Error(message); };
  const choose = async (id, text) => {
    await page.locator(`#${id}-trigger`).click();
    await page.locator(`#${id}-options`).getByRole("option", { name: text, exact: true }).click();
    assert(await page.locator(`#${id}-trigger`).getAttribute("aria-expanded") === "false", "Selecting an option must close the menu");
  };
  const pattern = `${origin}/api/**`;
  await page.route(pattern, routeApi);
  page.on("pageerror", onError);
  page.on("dialog", onDialog);
  try {
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto(`${origin}/#discover`);
    await page.evaluate(() => localStorage.clear());
    await page.reload();
    await page.locator("#repo-sort-trigger").waitFor({ state: "visible" });
    assert(await page.locator("select:visible").count() === 0, "Native selects must not be visible");
    assert(await page.locator("#repo-sort-trigger").evaluate(button => getComputedStyle(button).paddingRight) === "16px", "Dropdown arrow must keep 16px right padding");
    await page.locator("#repo-sort-trigger").focus();
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("End");
    await page.keyboard.press("Escape");
    assert(await page.locator("#repo-sort").inputValue() === "score", "Escape must not commit selection");
    await page.keyboard.press("Enter");
    await page.keyboard.press("End");
    await page.keyboard.press("Enter");
    assert(await page.locator("#repo-sort").inputValue() === "name", "Keyboard selection must commit");
    await page.locator("#repo-sort-trigger").click();
    await page.locator("#page-title").click();
    assert(await page.locator("#repo-sort-trigger").getAttribute("aria-expanded") === "false", "Click-away must close");
    await page.locator("#repo-sort-trigger").click();
    await page.evaluate(() => {
      const select = document.getElementById("repo-sort");
      select.options[0].textContent = "新的评分标签";
      select.dispatchEvent(new Event("change", { bubbles: true }));
    });
    assert(await page.locator("#repo-sort-trigger").getAttribute("aria-expanded") === "false", "Changed options must invalidate an open menu");
    await page.locator("#repo-rows input[type=checkbox]").first().check();
    assert(await page.locator("#select-filtered").evaluate(input => input.indeterminate), "Mixed checkbox state must survive");
    assert(await page.locator("#select-filtered").evaluate(input => getComputedStyle(input).appearance) === "none", "Checkbox must use custom paint");
    await page.reload();
    await page.waitForFunction(() => document.getElementById("repo-sort-trigger").textContent === "仓库名称");
    reports.push("Desktop: hidden native selects, keyboard commit/cancel, outside dismiss, mixed checkbox, persisted selection");

    await page.locator('[data-page="settings"]').click();
    await page.waitForFunction(() => document.getElementById("download-mirror-trigger").textContent === "ghfast.top");
    await choose("download-mirror", "直连");
    await page.locator("#refresh-hours").fill("0");
    await page.locator("#settings-save").click();
    assert(await page.locator("#refresh-hours-error").isVisible(), "Invalid input must get themed feedback");
    assert(!writes.some(write => write.path === "/api/settings"), "Invalid input must not save");
    await page.locator("#refresh-hours").fill("7");
    assert(await page.locator("#refresh-hours-error").count() === 0, "Valid input must clear field error");
    await page.locator("#settings-save").click();
    await page.waitForFunction(() => document.getElementById("settings-message").textContent.includes("设置已保存"));
    assert(writes.find(write => write.path === "/api/settings")?.body.downloadMirror === "", "Custom select must submit canonical value");
    await page.evaluate(() => { document.getElementById("settings-fields").disabled = true; });
    await page.waitForFunction(() => document.getElementById("download-mirror-trigger").disabled);
    await page.evaluate(() => { document.getElementById("settings-fields").disabled = false; });
    await page.waitForFunction(() => !document.getElementById("download-mirror-trigger").disabled);
    await page.locator("#github-save").click();
    await page.locator("#confirmation-dialog").waitFor({ state: "visible" });
    assert(await page.locator("#confirmation-cancel").evaluate(button => button === document.activeElement), "Safe action gets initial focus");
    await page.keyboard.press("Shift+Tab");
    assert(await page.locator("#confirmation-dialog").evaluate(dialog => dialog.contains(document.activeElement)), "Modal must contain keyboard focus");
    await page.keyboard.press("Escape");
    assert(await page.locator("#github-save").evaluate(button => button === document.activeElement), "Cancel restores focus");
    assert(!writes.some(write => write.path === "/api/connections/github"), "Cancelled confirmation must not save");
    await choose("github-mode", "Web 自定义");
    await page.locator("#github-save").click();
    assert(await page.locator("#github-token-error").isVisible(), "Required token validation is retained");
    await choose("github-mode", "禁用 Token");
    await page.locator("#github-save").click();
    await page.locator("#confirmation-accept").click();
    await page.waitForFunction(() => document.getElementById("github-message").textContent.includes("配置已保存"));
    assert(writes.filter(write => write.path === "/api/connections/github").length === 1, "Accept sends exactly one write");
    reports.push("Settings: dynamic mirror, validation, canonical submit, inherited disabled state, modal focus/cancel/accept");

    await page.locator('[data-page="checker"]').click();
    await page.getByText("检测参数与来源", { exact: true }).click();
    await page.locator("#checker-config-read").click();
    await page.locator("#checker-field-speed-test-trigger").waitFor({ state: "visible" });
    await choose("checker-field-speed-test", "启用");
    await page.locator("#checker-config-save").click();
    await page.locator("#confirmation-cancel").click();
    assert(!writes.some(write => write.path === "/api/checker/config"), "Dynamic config cancel must not save");
    await page.waitForFunction(() => document.activeElement === document.getElementById("checker-config-save"), null, { timeout: 2000 });
    await page.locator("#checker-config-save").click();
    await page.locator("#confirmation-accept").click();
    await page.waitForFunction(() => document.getElementById("checker-config-message").textContent.includes("参数已保存"));
    assert(writes.find(write => write.path === "/api/checker/config")?.body.values["speed-test"] === true, "Dynamic boolean must preserve type");
    reports.push("Checker: dynamic select enhancement, typed config submit and cancellation");

    await page.setViewportSize({ width: 375, height: 812 });
    await page.locator('[data-page="settings"]').click();
    await page.locator("#github-mode-trigger").click();
    const box = await page.locator("#github-mode-options").boundingBox();
    assert(box.x >= 0 && box.x + box.width <= 375 && box.y >= 0 && box.y + box.height <= 812, "Narrow-screen popup must remain within viewport");
    await page.keyboard.press("Escape");
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), "Narrow-screen layout must not overflow");
    await page.locator("#github-save").click();
    await page.locator("#confirmation-dialog").waitFor({ state: "visible" });
    await page.evaluate(() => { location.hash = "history"; });
    await page.locator("#confirmation-dialog").waitFor({ state: "hidden" });
    assert(writes.filter(write => write.path === "/api/connections/github").length === 1, "Navigation cancels pending write");
    await page.locator("#logs-clear").click();
    await page.locator("#confirmation-dialog").waitFor({ state: "visible" });
    authenticated = false;
    expire = true;
    await page.locator("#login-view").waitFor({ state: "visible", timeout: 10000 });
    assert(await page.locator("#confirmation-dialog").isHidden(), "Session expiry dismisses confirmation");
    assert(!writes.some(write => write.path === "/api/logs"), "Expired confirmation must not write");
    await page.locator("#login-button").click();
    assert(await page.locator("#password-error").isVisible(), "Login validation uses themed feedback");
    reports.push("375px: popup bounds and no overflow; navigation/session expiry cancel; login validation");
    assert(failures.length === 0, failures.join("; "));
    return reports;
  } finally {
    await page.unroute(pattern, routeApi);
    page.off("pageerror", onError);
    page.off("dialog", onDialog);
  }
}
