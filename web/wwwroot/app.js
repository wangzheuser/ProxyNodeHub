"use strict";

function safeExternalUrl(value) {
  try {
    const url = new URL(value);
    return ["http:", "https:"].includes(url.protocol) && !url.username && !url.password ? url.href : null;
  } catch {
    return null;
  }
}

function filterRepositories(repositories, query, sort, direction = sort === "name" ? "asc" : "desc", filters = {}) {
  const term = query.trim().toLocaleLowerCase();
  const visible = repositories.filter(repo => `${repo.fullName} ${repo.description || ""}`.toLocaleLowerCase().includes(term)
    && (filters.minCommits === "" || filters.minCommits == null || repo.commitsLast7Days / 7 >= Number(filters.minCommits))
    && (filters.minAge === "" || filters.minAge == null || repo.ageDays >= Number(filters.minAge))
    && (filters.maxInactive === "" || filters.maxInactive == null || repo.daysInactive <= Number(filters.maxInactive)));
  const comparators = {
    score: (a, b) => a.score - b.score,
    nodes: (a, b) => a.totalNodes - b.totalNodes,
    stars: (a, b) => a.stars - b.stars,
    commits: (a, b) => a.commitsLast7Days - b.commitsLast7Days,
    age: (a, b) => a.ageDays - b.ageDays,
    inactive: (a, b) => a.daysInactive - b.daysInactive,
    processing: (a, b) => (a.processingType || "").localeCompare(b.processingType || ""),
    links: (a, b) => a.links.length - b.links.length,
    updated: (a, b) => new Date(a.lastPush) - new Date(b.lastPush),
    name: (a, b) => a.fullName.localeCompare(b.fullName),
  };
  const compare = comparators[sort] || comparators.score;
  return visible.sort((a, b) => compare(a, b) * (direction === "asc" ? 1 : -1) || a.fullName.localeCompare(b.fullName));
}

function repositoriesForScope(repositories, visible, selected, scope) {
  return scope === "filtered" ? visible : scope === "selected" ? repositories.filter(repo => selected.has(repo.fullName)) : repositories;
}

function formatDate(value, short = false) {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "时间不可用";
  return new Intl.DateTimeFormat("zh-CN", {
    ...(short ? {} : { year: "numeric" }), month: "2-digit", day: "2-digit",
    hour: "2-digit", minute: "2-digit", hour12: false,
  }).format(date);
}

function filterCheckerNodes(nodes, query, sort = "original", positiveOnly = false) {
  const term = query.trim().toLocaleLowerCase();
  const visible = nodes.filter(node => `${node.name} ${node.type} ${node.country || ""} ${node.subTag || ""}`.toLocaleLowerCase().includes(term) && (!positiveOnly || node.speed > 0));
  if (sort.startsWith("speed-")) visible.sort((a, b) => (Number(a.speed || 0) - Number(b.speed || 0)) * (sort === "speed-asc" ? 1 : -1));
  if (sort === "name") visible.sort((a, b) => (a.name || "").localeCompare(b.name || ""));
  return visible;
}

function retainedSources(nodes) {
  const sources = new Map();
  for (const node of nodes) {
    const tag = node.subTag || "未标注来源";
    if (!sources.has(tag)) sources.set(tag, { tag, count: 0, speedSamples: 0, speedSum: 0, maxSpeed: null });
    const source = sources.get(tag);
    source.count += 1;
    const speed = Number(node.speed);
    if (Number.isFinite(speed) && speed > 0) {
      source.speedSamples += 1;
      source.speedSum += speed;
      source.maxSpeed = Math.max(source.maxSpeed || 0, speed);
    }
  }
  return [...sources.values()].map(({ speedSum, ...source }) => ({ ...source, averageSpeed: source.speedSamples ? speedSum / source.speedSamples : null }))
    .sort((a, b) => b.count - a.count || a.tag.localeCompare(b.tag));
}

function sourceRepository(tag, repositories) {
  return repositories.find(repo => repo.fullName.toLocaleLowerCase() === tag.toLocaleLowerCase());
}

const viewPreferenceRules = {
  "min-commits": "number", "min-age": "integer", "max-inactive": "integer",
  "repo-sort": ["score", "nodes", "updated", "stars", "commits", "age", "inactive", "processing", "links", "name"],
  "repo-direction": ["asc", "desc"], "repo-extra-columns": "boolean",
  "node-sort": ["original", "speed-desc", "speed-asc", "name"], "node-positive-speed": "boolean",
};

function viewPreferences(value) {
  const saved = {};
  if (!value || typeof value !== "object" || Array.isArray(value)) return saved;
  for (const [key, rule] of Object.entries(viewPreferenceRules)) {
    const item = value[key];
    if (Array.isArray(rule) ? rule.includes(item) : rule === "boolean" ? typeof item === "boolean"
      : typeof item === "string" && (item === "" || /^\d+(\.\d+)?$/.test(item) && Number.isFinite(Number(item)) && (rule !== "integer" || Number.isSafeInteger(Number(item))))) saved[key] = item;
  }
  return saved;
}

function checkerPage(nodes, page) {
  const pageCount = Math.max(1, Math.ceil(nodes.length / 100));
  const index = Math.min(Math.max(0, page), pageCount - 1);
  return { index, pageCount, items: nodes.slice(index * 100, (index + 1) * 100) };
}

function repoCategoryLabel(value) {
  return ["节点提供仓库", "订阅链接聚合", "间接引用", "非节点仓库"][value] ?? "未分类";
}

if (typeof document === "undefined") {
  // Run with: node web/wwwroot/app.js --self-test (no browser dependencies).
  if (typeof process !== "undefined" && process.argv.includes("--self-test")) {
    const assert = require("node:assert/strict");
    assert.equal(safeExternalUrl("javascript:alert(1)"), null);
    assert.equal(safeExternalUrl("data:text/html,test"), null);
    assert.equal(safeExternalUrl("https://user:password@example.test/"), null);
    assert.equal(safeExternalUrl("/relative"), null);
    assert.equal(safeExternalUrl("https://github.com/a/b"), "https://github.com/a/b");
    assert.equal(safeExternalUrl("http://192.168.31.122:8199"), "http://192.168.31.122:8199/");
    const source = [
      { fullName: "b/Repo", description: "节点来源", score: 30, totalNodes: 20, stars: 5, commitsLast7Days: 14, ageDays: 200, daysInactive: 6, processingType: "B", links: [{}, {}], lastPush: "2026-10-01T00:00:00Z" },
      { fullName: "a/Repo", description: null, score: 90, totalNodes: 10, stars: 1, commitsLast7Days: 7, ageDays: 100, daysInactive: 4, processingType: "A", links: [{}], lastPush: "2026-10-03T00:00:00Z" },
    ];
    assert.equal(filterRepositories(source, "  REPO  ", "score")[0].fullName, "a/Repo");
    assert.equal(filterRepositories(source, "节点", "nodes").length, 1);
    assert.equal(filterRepositories(source, "", "updated")[0].fullName, "a/Repo");
    assert.equal(filterRepositories(source, "", "stars")[0].fullName, "b/Repo");
    assert.equal(filterRepositories(source, "", "processing", "asc")[0].fullName, "a/Repo");
    assert.equal(filterRepositories(source, "", "links", "desc")[0].fullName, "b/Repo");
    assert.equal(filterRepositories(source, "missing", "name").length, 0);
    assert.equal(source[0].fullName, "b/Repo", "Sorting must not mutate the server snapshot");
    assert.equal(filterRepositories(source, "", "score", "asc")[0].fullName, "b/Repo");
    assert.deepEqual(filterRepositories(source, "", "commits", "desc", { minCommits: 2, minAge: 200, maxInactive: 6 }).map(r => r.fullName), ["b/Repo"]);
    assert.equal(filterRepositories(source, "", "age", "asc", { maxInactive: 0 }).length, 0, "Zero is a real threshold");
    assert.equal(filterRepositories(source, "", "inactive", "desc", { minCommits: "", minAge: "", maxInactive: "" }).length, 2);
    assert.deepEqual(repositoriesForScope(source, [], new Set(["a/Repo"]), "selected").map(r => r.fullName), ["a/Repo"]);
    assert.equal(repositoriesForScope(source, [], new Set(), "all").length, 2);
    assert.equal(repositoriesForScope(source, [], new Set(), "filtered").length, 0);
    assert.equal(formatDate(null), "—");
    assert.equal(formatDate("invalid"), "时间不可用");
    assert.equal(filterCheckerNodes([{ name: "测试节点", type: "vless", country: "JP" }], " jp ").length, 1);
    assert.equal(filterCheckerNodes([{ name: "测试节点", type: "vless" }], "trojan").length, 0);
    const nodes = [{ name: "b", speed: 0, subTag: "来源甲" }, { name: "a", speed: 30, subTag: "来源甲" }, { name: "c", speed: 10 }];
    assert.deepEqual(filterCheckerNodes(nodes, "", "speed-desc", true).map(n => n.speed), [30, 10]);
    assert.equal(filterCheckerNodes(nodes, "来源甲").length, 2, "Zero speed is retained unless explicitly filtered");
    assert.deepEqual(retainedSources(nodes), [
      { tag: "来源甲", count: 2, speedSamples: 1, maxSpeed: 30, averageSpeed: 30 },
      { tag: "未标注来源", count: 1, speedSamples: 1, maxSpeed: 10, averageSpeed: 10 },
    ]);
    assert.deepEqual(retainedSources([{ speed: 0 }, { speed: -1 }, {}]), [{ tag: "未标注来源", count: 3, speedSamples: 0, maxSpeed: null, averageSpeed: null }]);
    assert.equal(retainedSources([{ speed: 10 }, { speed: 30 }, { speed: 0 }])[0].averageSpeed, 20, "Mean only includes positive speed samples");
    assert.equal(sourceRepository("B/repo", source), source[0]);
    assert.equal(sourceRepository("unmatched", source), undefined);
    assert.deepEqual(viewPreferences({ "min-commits": "0", "min-age": "2", "repo-sort": "links", "repo-extra-columns": true, "github-token": "must-not-persist", "checker-manual-urls": "https://private.test/token" }), { "min-commits": "0", "min-age": "2", "repo-sort": "links", "repo-extra-columns": true });
    assert.deepEqual(viewPreferences({ "min-age": "0.5", "max-inactive": "-1", "repo-sort": "unknown", "repo-extra-columns": "true", "min-commits": "Infinity" }), {});
    assert.deepEqual(viewPreferences(null), {});
    assert.equal(checkerPage(Array.from({ length: 201 }, (_, i) => i), 1).items.length, 100);
    assert.deepEqual(checkerPage(Array.from({ length: 201 }, (_, i) => i), 5).items, [200]);
    assert.deepEqual(checkerPage([], 1), { index: 0, pageCount: 1, items: [] });
    assert.equal(repoCategoryLabel(0), "节点提供仓库");
    assert.equal(repoCategoryLabel(3), "非节点仓库");
    console.log("Web UI self-check passed: URL boundaries, filtering, sorting, dates, pagination, category mapping.");
  }
} else {
  startWorkspace();
}

function startWorkspace() {
  const controls = createWorkspaceControls();
  const $ = id => document.getElementById(id);
  const text = (id, value) => { if ($(id).textContent !== String(value)) $(id).textContent = value; };
  const make = (tag, className, value) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (value !== undefined) node.textContent = value;
    return node;
  };
  const pages = {
    discover: ["01 / DISCOVERY", "发现仓库", "从持续更新的仓库中，找到值得保留的来源。"],
    favorites: ["02 / COLLECTION", "我的收藏", "留下值得持续关注的来源，随时查看与导出。"],
    checker: ["03 / VALIDATION", "订阅检测", "来源发现与节点检测，各司其职。"],
    history: ["04 / ACTIVITY", "运行记录", "每次尝试都有记录，失败也不例外。"],
    features: ["05 / KNOWLEDGE", "学习记录", "让下一次发现，沿着已经验证过的路径。"],
    settings: ["06 / PREFERENCES", "服务设置", "控制发现节奏，密钥始终留在服务端。"],
  };
  let csrfToken = "";
  let authenticated = false;
  let state = null;
  let page = "discover";
  let selectedRepo = null;
  let settingsDirty = false;
  let pollTimer = null;
  let stateRequest = null;
  let authGeneration = 0;
  let runRequest = false;
  let cancelRequest = false;
  let exporting = false;
  let tableSignature = "";
  let detailSignature = "";
  let historySignature = "";
  let featureData = null;
  let featureRequest = false;
  let checkerRequest = false;
  let checkerConnection = null;
  let checkerStatus = null;
  let checkerResults = null;
  let selectedNode = null;
  let checkerRunRequest = false;
  let nodePage = 0;
  let nodeTableSignature = "";
  const selectedRepos = new Set();
  let connections = null;
  let connectionsRequest = false;
  let logData = null;
  let logRequest = 0;
  let checkerConfig = null;
  const settingsFields = {
    refreshHours: "refresh-hours", repoCount: "repo-count", inactiveDays: "inactive-days",
    analysisConcurrency: "analysis-concurrency", logRetentionDays: "log-retention-days",
    searchHistoryDays: "search-history-days", downloadMirror: "download-mirror",
    autoRefresh: "auto-refresh", skipSearched: "skip-searched", skipFavorites: "skip-favorites",
  };
  const viewPreferenceKey = "proxynodehub.view.v1";
  const viewPreferenceNote = $("view-preferences-note").textContent;
  let viewPreferenceIssue = "";

  function reportPreferenceIssue(message) {
    text("view-preferences-note", message);
    if (authenticated && viewPreferenceIssue !== message) notice(message, true);
    viewPreferenceIssue = message;
  }

  function restoreViewPreferences() {
    try {
      const saved = viewPreferences(JSON.parse(localStorage.getItem(viewPreferenceKey)));
      for (const [id, value] of Object.entries(saved)) {
        if ($(id).type === "checkbox") $(id).checked = value;
        else $(id).value = value;
      }
    } catch {
      reportPreferenceIssue("无法读取本机显示偏好，已使用默认值；本次仍可正常筛选。数值筛选对发现与收藏都生效。");
    }
  }

  function saveViewPreferences() {
    const saved = viewPreferences(Object.fromEntries(Object.keys(viewPreferenceRules).map(id => [id, $(id).type === "checkbox" ? $(id).checked : $(id).value])));
    try {
      localStorage.setItem(viewPreferenceKey, JSON.stringify(saved));
      viewPreferenceIssue = "";
      text("view-preferences-note", viewPreferenceNote);
    }
    catch {
      reportPreferenceIssue("浏览器不允许保存显示偏好；本次筛选仍有效，刷新后可能恢复默认。数值筛选对发现与收藏都生效。");
    }
  }

  function current(generation) { return authenticated && generation === authGeneration; }

  async function confirmChange(message, generation = authGeneration) {
    return await controls.confirm(message) && current(generation);
  }

  function clearPrivateInputs() {
    for (const id of ["github-token", "checker-api-key", "checker-manual-urls", "manual-copy-text"]) $(id).value = "";
    $("manual-copy").hidden = true;
    $("checker-config-fields").replaceChildren();
    $("checker-config-form").hidden = $("checker-sources-form").hidden = true;
    checkerConfig = null;
  }

  async function action(button, operation, messageId) {
    if (button.disabled || button.dataset.busyLabel || !authenticated) return;
    const generation = authGeneration;
    const restoreFocus = document.activeElement === button;
    const actionPage = page;
    const label = button.textContent;
    button.disabled = true;
    button.dataset.busyLabel = label;
    button.textContent = "正在处理…";
    if (messageId) text(messageId, "正在处理…");
    try { await operation(generation); }
    catch (error) {
      if (current(generation)) {
        if (messageId) text(messageId, error.message);
        else notice(error.message, true);
      }
    } finally {
      if (current(generation)) {
        delete button.dataset.busyLabel;
        button.disabled = false;
        button.textContent = label;
        if (state) {
          renderState();
          if (page === "checker") renderCheckerData();
        }
        if (restoreFocus) requestAnimationFrame(() => {
          const focus = document.activeElement;
          if (current(generation) && page === actionPage && button.isConnected && !button.disabled
            && (focus === document.body || focus?.closest("#confirmation-dialog:not([open])")))
            button.focus({ preventScroll: true });
        });
      }
    }
  }

  function download(blob, name) {
    const url = URL.createObjectURL(blob);
    const link = make("a");
    link.href = url;
    link.download = name;
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  async function copyText(value, generation) {
    if (!value) throw new Error("所选仓库没有可复制的内容。");
    if (navigator.clipboard && window.isSecureContext) {
      try {
        await navigator.clipboard.writeText(value);
        if (current(generation)) notice("已复制到剪贴板。");
        return;
      } catch { /* Expose manual copying when clipboard permission is denied. */ }
    }
    if (!current(generation)) return;
    $("manual-copy").hidden = false;
    $("manual-copy-text").value = value;
    $("manual-copy-text").focus();
    $("manual-copy-text").select();
    notice("内容已选中，请使用系统复制操作。");
  }

  function notice(message, error = false) {
    text("action-notice-text", message);
    $("action-notice").classList.toggle("error-notice", error);
    $("action-notice").hidden = false;
  }

  function showLogin(message = "") {
    controls.dismiss();
    authenticated = false;
    authGeneration += 1;
    clearTimeout(pollTimer);
    clearPrivateInputs();
    connections = checkerConfig = null;
    selectedRepos.clear();
    state = featureData = checkerStatus = checkerResults = checkerConnection = selectedNode = null;
    stateRequest = null;
    featureRequest = checkerRequest = connectionsRequest = runRequest = cancelRequest = exporting = checkerRunRequest = false;
    settingsDirty = false;
    nodePage = 0;
    tableSignature = detailSignature = historySignature = nodeTableSignature = "";
    logData = null;
    logRequest += 1;
    for (const button of document.querySelectorAll("[data-busy-label]")) {
      button.textContent = button.dataset.busyLabel;
      button.disabled = false;
      delete button.dataset.busyLabel;
    }
    for (const fields of document.querySelectorAll("fieldset")) fields.disabled = false;
    for (const id of ["github-save", "connection-save", "features-refresh", "checker-refresh", "log-run"]) $(id).disabled = false;
    text("settings-save", "保存设置");
    $("action-notice").hidden = true;
    $("workspace").hidden = true;
    $("login-view").hidden = false;
    $("login-button").disabled = !csrfToken;
    text("login-button", "进入工作台");
    text("login-error", message);
    $("login-error").hidden = !message;
    $("password").value = "";
  }

  async function request(path, options = {}) {
    const generation = authGeneration;
    const { timeout = 30000, raw = false, ...fetchOptions } = options;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeout);
    const headers = { Accept: "application/json", ...fetchOptions.headers };
    if (fetchOptions.method && fetchOptions.method !== "GET") headers["X-CSRF-TOKEN"] = csrfToken;
    if (fetchOptions.body !== undefined) {
      headers["Content-Type"] = "application/json";
      fetchOptions.body = JSON.stringify(fetchOptions.body);
    }
    try {
      const response = await fetch(path, { ...fetchOptions, headers, credentials: "same-origin", cache: "no-store", signal: controller.signal });
      if (!response.ok) {
        const body = await response.text();
        let detail;
        try { const problem = JSON.parse(body); detail = problem.detail || problem.title; } catch { /* HTTP status remains the error for non-JSON responses. */ }
        if (response.status === 401 && generation === authGeneration && !["/api/login", "/api/session"].includes(path)) await expireSession();
        throw new Error(detail || (response.status === 401 ? "密码不正确，请重试。" : response.status === 429 ? "请求过于频繁，请稍后重试。" : `请求失败（HTTP ${response.status}），请检查服务日志。`));
      }
      if (raw) return await response.blob();
      if (response.status === 204 || response.status === 202) return null;
      return await response.json();
    } catch (error) {
      if (error.name === "AbortError") throw new Error("请求超时，请检查服务状态后重试。后台任务可能仍在运行。");
      if (error instanceof TypeError) throw new Error("无法连接服务，请检查内网连接或容器状态。");
      throw error;
    } finally {
      clearTimeout(timer);
    }
  }

  async function expireSession() {
    const message = "登录已过期，请重新输入管理员密码。";
    showLogin(message);
    csrfToken = "";
    $("login-button").disabled = true;
    text("login-button", "正在更新登录会话…");
    try {
      const session = await request("/api/session");
      csrfToken = session.csrfToken;
      $("login-button").disabled = false;
      text("login-button", "进入工作台");
    } catch (error) {
      text("login-error", `${message} ${error.message}`);
      $("session-retry").hidden = false;
    }
  }

  async function getSession() {
    $("session-retry").hidden = true;
    $("login-button").disabled = true;
    text("login-button", "正在连接服务…");
    try {
      const session = await request("/api/session");
      csrfToken = session.csrfToken;
      if (session.authenticated) await enterWorkspace();
      else showLogin();
    } catch (error) {
      showLogin(error.message);
      $("login-button").disabled = true;
      $("session-retry").hidden = false;
    }
  }

  async function enterWorkspace() {
    authenticated = true;
    authGeneration += 1;
    $("login-view").hidden = true;
    $("workspace").hidden = false;
    $("password").value = "";
    navigate();
    await loadState();
    if (authenticated && viewPreferenceIssue) notice(viewPreferenceIssue, true);
    schedulePoll();
  }

  function schedulePoll() {
    clearTimeout(pollTimer);
    if (!authenticated) return;
    pollTimer = setTimeout(async () => {
      await loadState();
      if (authenticated && page === "checker" && checkerStatus?.checking) await pollCheckerStatus();
      schedulePoll();
    }, 4000);
  }

  async function loadState() {
    if (!authenticated) return;
    if (stateRequest) return stateRequest;
    const generation = authGeneration;
    stateRequest = (async () => {
      try {
        const next = await request("/api/state");
        if (!authenticated || generation !== authGeneration) return;
        state = next;
        $("connection-error").hidden = true;
        text("connection-label", "服务已连接");
        $("connection-label").classList.remove("is-error");
        renderState();
      } catch (error) {
        if (!authenticated || generation !== authGeneration) return;
        text("connection-error-text", `${error.message}${state ? " 页面保留上次读取的结果；当前后台状态尚未确认。" : " 尚未取得服务状态。"}`);
        $("connection-error").hidden = false;
        text("connection-label", "连接中断");
        $("connection-label").classList.add("is-error");
      } finally {
        if (generation === authGeneration) stateRequest = null;
      }
    })();
    return stateRequest;
  }

  function navigate() {
    controls.dismiss();
    const requested = location.hash.slice(1);
    clearPrivateInputs();
    page = Object.hasOwn(pages, requested) ? requested : "discover";
    document.querySelectorAll("[data-page]").forEach(link => {
      if (link.dataset.page === page) link.setAttribute("aria-current", "page");
      else link.removeAttribute("aria-current");
    });
    const [kicker, title, description] = pages[page];
    text("page-kicker", kicker);
    text("page-title", title);
    text("page-description", description);
    document.title = `${title} · ProxyNodeHub`;
    $("repositories-panel").hidden = !["discover", "favorites"].includes(page);
    for (const name of ["settings", "history", "features", "checker"]) $(name + "-panel").hidden = page !== name;
    if (state) renderState();
    if (authenticated && page === "features") loadFeatures();
    if (authenticated && page === "settings") { loadConnections(); loadMirrors(); }
  }

  function renderState() {
    const attempt = state.attempt;
    text("nav-repo-count", state.repositories.length);
    text("nav-favorite-count", state.favorites.length);
    text("last-result", state.generatedAt ? formatDate(state.generatedAt, true) : "尚无有效结果");
    text("next-run", state.settings.autoRefresh ? state.nextRunAt ? formatDate(state.nextRunAt, true) : attempt.running ? "本轮完成后安排" : "等待服务端安排" : "已关闭自动发现");
    text("run-title", attempt.running ? "后台正在发现" : attempt.error ? "本次发现未完成" : state.generatedAt ? "上次有效结果已保留" : "等待首次发现");
    text("run-description", attempt.running ? `${attempt.phase || "正在处理"}${attempt.total > 0 ? ` · ${attempt.completed} / ${attempt.total}` : ""} · 关闭页面不影响任务` : attempt.error || (state.generatedAt ? "可继续筛选、收藏与导出；新结果完成后自动更新。" : "开始发现后，将搜索并分析公开 GitHub 仓库。"));
    $("run-indicator").className = `status-dot${attempt.running ? " is-running" : state.generatedAt ? " is-ready" : ""}`;
    $("run-progress").hidden = !attempt.running;
    if (attempt.total > 0) {
      $("run-progress").max = attempt.total;
      $("run-progress").value = Math.max(0, Math.min(attempt.completed, attempt.total));
    } else $("run-progress").removeAttribute("value");
    $("refresh-button").disabled = attempt.running || runRequest;
    $("explore-button").disabled = attempt.running || runRequest || Boolean($("explore-button").dataset.busyLabel);
    text("refresh-button", runRequest ? "正在提交…" : attempt.running ? "发现进行中…" : "↻ 开始发现");
    $("cancel-button").hidden = !attempt.running;
    $("cancel-button").disabled = cancelRequest;
    text("cancel-button", cancelRequest ? "正在取消…" : "取消本次发现");
    text("token-status", state.tokenConfigured ? "已配置 · 服务端使用" : "未配置 · 使用公开访问额度");
    $("token-status").classList.toggle("is-ready", state.tokenConfigured);
    if (!settingsDirty && !$("settings-form").contains(document.activeElement)) {
      const mirror = $("download-mirror");
      const savedMirror = state.settings.downloadMirror;
      // Keep the saved value selectable even if the mirror catalog is slow or unavailable.
      if (![...mirror.options].some(option => option.value === savedMirror))
        mirror.add(new Option(`${savedMirror} · 已保存`, savedMirror));
      for (const [key, id] of Object.entries(settingsFields)) {
        if ($(id).type === "checkbox") $(id).checked = state.settings[key];
        else $(id).value = state.settings[key];
      }
    }
    $("settings-save").disabled = !settingsDirty || $("settings-fields").disabled;
    renderCheckerConnection();
    const failures = state.failures;
    $("source-warning").hidden = !failures || !(failures.searches || failures.repositories || failures.downloads);
    if (failures) text("source-warning", `上次有效结果包含部分来源失败：搜索 ${failures.searches} 次、仓库 ${failures.repositories} 个、下载 ${failures.downloads} 次。已成功的来源仍可使用，详情见运行记录。`);
    if (["discover", "favorites"].includes(page)) renderRepositories();
    if (page === "history") renderHistory();
    if (page === "checker" && state.checker.configured && checkerConnection === null) readChecker();
    controls.refresh();
  }

  function externalLink(label, url, className = "") {
    const safe = safeExternalUrl(url);
    if (!safe) return make("span", "muted", "链接地址不可用");
    const link = make("a", className, label);
    link.href = safe;
    link.target = "_blank";
    link.rel = "noopener noreferrer";
    return link;
  }

  function favoriteButton(repo) {
    const favorite = state.favorites.some(item => item.fullName === repo.fullName);
    const button = make("button", "icon-button star-button", favorite ? "★" : "☆");
    button.type = "button";
    button.setAttribute("aria-pressed", String(favorite));
    button.setAttribute("aria-label", `${favorite ? "取消收藏" : "收藏"} ${repo.fullName}`);
    button.addEventListener("click", async () => {
      const generation = authGeneration;
      button.disabled = true;
      try {
        await request("/api/favorites", { method: "PUT", body: { fullName: repo.fullName, favorite: !favorite } });
        if (!current(generation)) return;
        await loadState();
        if (current(generation)) notice(favorite ? `已取消收藏 ${repo.fullName}。` : `已收藏 ${repo.fullName}。`);
      } catch (error) { if (current(generation)) notice(error.message, true); }
      finally { button.disabled = false; }
    });
    return button;
  }

  function repositoryView() {
    const repos = page === "favorites" ? state.favorites : state.repositories;
    const filters = { minCommits: $("min-commits").value, minAge: $("min-age").value, maxInactive: $("max-inactive").value };
    const visible = filterRepositories(repos, $("repo-search").value, $("repo-sort").value, $("repo-direction").value, filters);
    return { repos, visible, filtering: Boolean($("repo-search").value.trim()) || Object.values(filters).some(value => value !== "") };
  }

  function renderRepositories() {
    const { repos, visible, filtering } = repositoryView();
    const selected = repositoriesForScope(repos, visible, selectedRepos, "selected");
    const selectedVisible = visible.filter(repo => selectedRepos.has(repo.fullName)).length;
    $("select-filtered").checked = visible.length > 0 && selectedVisible === visible.length;
    $("select-filtered").indeterminate = selectedVisible > 0 && selectedVisible !== visible.length;
    $("select-filtered").disabled = visible.length === 0;
    text("selected-count", `已选 ${selected.length} 个${selected.length > selectedVisible ? ` · ${selected.length - selectedVisible} 个不在当前筛选` : ""}`);
    for (const id of ["clear-selection", "copy-links", "copy-names", "bulk-favorite", "bulk-unfavorite", "bulk-recheck"]) $(id).disabled = !selected.length || Boolean($(id).dataset.busyLabel);
    $("bulk-recheck").disabled ||= state.attempt.running;
    text("collection-summary", `${visible.length} 个${page === "favorites" ? "收藏" : "仓库"}${filtering ? ` / 共 ${repos.length} 个` : ""}`);
    text("export-button", exporting ? "正在导出…" : "导出");
    $("export-button").disabled = exporting || repositoriesForScope(repos, visible, selectedRepos, $("export-scope").value).length === 0;
    $("repo-table").classList.toggle("show-extra-columns", $("repo-extra-columns").checked);
    const signature = JSON.stringify([page, visible, state.favorites.map(item => item.fullName), selectedRepo, selected]);
    if (signature !== tableSignature) {
      const focused = document.activeElement?.dataset.repoFocus;
      const fragment = document.createDocumentFragment();
      for (const repo of visible) {
        const row = make("tr", selectedRepo === repo.fullName ? "is-selected" : "");
        const favorite = make("td", "favorite-cell");
        const selectLabel = make("label", "repo-select");
        const select = make("input");
        select.type = "checkbox";
        select.checked = selectedRepos.has(repo.fullName);
        select.setAttribute("aria-label", `选择 ${repo.fullName}`);
        select.dataset.repoFocus = `select:${repo.fullName}`;
        select.addEventListener("change", () => {
          if (select.checked) selectedRepos.add(repo.fullName);
          else selectedRepos.delete(repo.fullName);
          renderRepositories();
        });
        selectLabel.append(select);
        const star = favoriteButton(repo);
        star.dataset.repoFocus = `favorite:${repo.fullName}`;
        favorite.append(selectLabel, star);
        const name = make("td", "name-cell");
        const button = make("button", "repo-name-button", repo.fullName);
        button.type = "button";
        button.dataset.repoFocus = `detail:${repo.fullName}`;
        button.setAttribute("aria-controls", "repo-detail");
        button.setAttribute("aria-pressed", String(selectedRepo === repo.fullName));
        button.addEventListener("click", () => {
          selectedRepo = repo.fullName;
          renderRepositories();
          if (matchMedia("(max-width: 1349px)").matches) {
            $("detail-title")?.focus({ preventScroll: true });
            $("repo-detail").scrollIntoView({ block: "start", behavior: "instant" });
          }
        });
        name.append(button, make("p", "repo-description", repo.description || "暂无仓库描述"));
        const score = make("td", "score-cell");
        score.dataset.label = "评分";
        score.append(make("span", `score-number${repo.score >= 70 ? " score-high" : ""}`, repo.score));
        const nodes = make("td", "nodes-cell", Number(repo.totalNodes).toLocaleString("zh-CN"));
        nodes.dataset.label = "节点";
        row.append(favorite, name, score, nodes, make("td", "updated-cell", formatDate(repo.lastPush, true)));
        for (const [label, value] of [["Star", repo.stars], ["日均提交", (repo.commitsLast7Days / 7).toFixed(1)], ["库龄 · 天", repo.ageDays], ["未更新 · 天", repo.daysInactive]]) {
          const cell = make("td", "extra-column", value);
          cell.dataset.label = label;
          row.append(cell);
        }
        fragment.append(row);
      }
      $("repo-rows").replaceChildren(fragment);
      tableSignature = signature;
      if (focused) [...$("repo-rows").querySelectorAll("[data-repo-focus]")].find(node => node.dataset.repoFocus === focused)?.focus({ preventScroll: true });
    }
    $("repo-empty").hidden = visible.length !== 0;
    $("clear-filter").hidden = !filtering;
    text("repo-empty-title", filtering ? "没有匹配的仓库" : page === "favorites" ? "把值得保留的来源放在这里" : "还没有仓库档案");
    text("repo-empty-description", filtering ? "换一个名称或描述关键词，或清除筛选查看全部来源。" : page === "favorites" ? "在发现列表中点击星标，即可收藏仓库。收藏独立保留，不随新一轮发现消失。" : "点击「开始发现」，服务会搜索公开仓库；成功后结果自动出现在这里。");
    renderDetail();
  }

  function renderDetail() {
    const repo = [...state.repositories, ...state.favorites].find(item => item.fullName === selectedRepo);
    const signature = JSON.stringify([repo, repo && state.favorites.some(item => item.fullName === repo.fullName)]);
    if (signature === detailSignature) return;
    detailSignature = signature;
    const detail = $("repo-detail");
    if (!repo) {
      detail.replaceChildren(make("p", "eyebrow", "REPOSITORY DOSSIER"), make("h2", "", "来源详情"), make("p", "muted", selectedRepo ? "选中的仓库已不在当前档案中，请选择其他来源。" : "选择列表中的仓库，查看更新时间、评分与发现的订阅链接。"), make("div", "detail-note", "先发现，再验证。订阅连通性与速度由 subs-check 检测。"));
      return;
    }
    const split = repo.fullName.indexOf("/");
    const title = make("h2", "", split >= 0 ? repo.fullName.slice(split + 1) : repo.fullName);
    title.id = "detail-title";
    title.tabIndex = -1;
    const actions = make("div", "detail-actions");
    actions.append(externalLink("查看 GitHub ↗", repo.url, "button text-button"), favoriteButton(repo));
    const stats = make("dl", "detail-stats");
    for (const [label, value] of [["来源评分", `${repo.score} / 100`], ["发现节点", Number(repo.totalNodes).toLocaleString("zh-CN")], ["GitHub Stars", repo.stars], ["近 7 日提交", repo.commitsLast7Days], ["处理类型", repo.processingType || "未分类"], ["默认分支", repo.branch || "—"], ["最近更新", formatDate(repo.lastPush, true)], ["活跃日期数", repo.distinctActiveDays]]) {
      const item = make("div");
      item.append(make("dt", "", label), make("dd", "", value));
      stats.append(item);
    }
    const links = make("ul", "detail-links");
    for (const link of repo.links) {
      const item = make("li");
      const linkActions = make("div", "subscription-actions");
      const copy = make("button", "button compact", "复制");
      copy.type = "button";
      copy.setAttribute("aria-label", `复制 ${link.name || link.type || "此订阅"} 链接`);
      copy.disabled = !safeExternalUrl(link.url);
      copy.addEventListener("click", () => action(copy, generation => copyText(safeExternalUrl(link.url), generation)));
      linkActions.append(externalLink(`${link.name || link.type || "订阅来源"} ↗`, link.url), copy);
      item.append(linkActions);
      const meta = make("div", "link-meta");
      meta.append(make("span", "", link.isValid ? "已读取来源" : link.isAnalyzed ? "未验证通过" : "尚未验证"), make("span", "", `${link.nodeCount} 节点`));
      item.append(meta);
      links.append(item);
    }
    detail.replaceChildren(make("p", "eyebrow", "REPOSITORY DOSSIER"), make("p", "detail-owner", split >= 0 ? repo.fullName.slice(0, split) : "GitHub"), title, make("p", "muted", repo.description || "此仓库暂无描述。"), actions, stats, make("h3", "", `订阅来源 · ${repo.links.length}`), links, make("p", "detail-note", "「已读取来源」仅表示解析成功，不代表其中节点在线或可用。"));
  }

  function renderHistory() {
    const displayedLogs = $("log-run").value ? logData || [] : state.logs;
    const signature = JSON.stringify([state.history, displayedLogs]);
    if (signature === historySignature) return;
    historySignature = signature;
    const history = document.createDocumentFragment();
    for (const entry of [...state.history].sort((a, b) => new Date(b.startedAt) - new Date(a.startedAt))) {
      const row = make("tr");
      const finished = make("td", "", formatDate(entry.finishedAt));
      if (entry.error) finished.append(make("small", "result-failed", entry.error));
      row.append(make("td", "", formatDate(entry.startedAt)), make("td", entry.success ? "result-success" : "result-failed", entry.success ? "已完成" : "未完成"), make("td", "", entry.subscriptions), finished);
      history.append(row);
    }
    $("history-rows").replaceChildren(history);
    $("history-empty").hidden = state.history.length !== 0;
    const selectedRun = $("log-run").value;
    const options = [new Option("当前日志", "")];
    for (const entry of [...state.history].sort((a, b) => new Date(b.startedAt) - new Date(a.startedAt))) options.push(new Option(formatDate(entry.startedAt), entry.startedAt));
    $("log-run").replaceChildren(...options);
    if (options.some(option => option.value === selectedRun)) $("log-run").value = selectedRun;
    const logs = document.createDocumentFragment();
    for (const entry of [...displayedLogs].sort((a, b) => new Date(b.at) - new Date(a.at))) {
      const item = make("li");
      const time = make("time", "", formatDate(entry.at));
      time.dateTime = entry.at;
      item.append(time, make("span", "", entry.message));
      logs.append(item);
    }
    $("log-list").replaceChildren(logs);
    $("logs-empty").hidden = displayedLogs.length !== 0;
    controls.refresh();
  }

  function logPath(downloadFile = false) {
    return `/api/logs${downloadFile ? "/download" : ""}${$("log-run").value ? `?startedAt=${encodeURIComponent($("log-run").value)}` : ""}`;
  }

  async function loadLogs(generation) {
    const requestNumber = ++logRequest;
    const selectedRun = $("log-run").value;
    $("log-run").disabled = true;
    try {
      const next = await request(logPath());
      if (!current(generation) || requestNumber !== logRequest || selectedRun !== $("log-run").value) return;
      logData = next;
      if (!selectedRun) state.logs = next;
      historySignature = "";
      renderHistory();
    } finally { if (current(generation)) $("log-run").disabled = false; }
  }

  async function loadSearchHistory(generation) {
    const records = await request("/api/search-history");
    if (!current(generation)) return;
    const fragment = document.createDocumentFragment();
    for (const entry of records) {
      const item = make("li");
      item.append(make("time", "", formatDate(entry.searchedAt)), make("span", "", entry.fullName));
      fragment.append(item);
    }
    $("search-history-list").replaceChildren(fragment);
    text("search-history-message", records.length ? `${records.length} 条搜索记忆；只影响探索排除，不删除仓库或收藏。` : "搜索记忆为空；下一次探索可重新检查这些来源。");
  }

  function connectionModeChanged(kind) {
    const github = kind === "github";
    const mode = $(github ? "github-mode" : "connection-mode").value;
    const custom = mode === "custom";
    const ids = github ? ["github-token"] : ["checker-api-url", "checker-web-url", "checker-api-key"];
    for (const id of ids) $(id).disabled = !custom;
    $(github ? "github-token" : "checker-api-url").required = custom;
    if (!github) $("checker-api-key").required = custom && !(connections?.checker.source === "custom" && connections.checker.keyConfigured && safeExternalUrl($("checker-api-url").value) === safeExternalUrl(connections.checker.apiUrl));
    if (!custom) $(github ? "github-token" : "checker-api-key").value = "";
  }

  async function loadConnections() {
    if (connectionsRequest) return;
    const generation = authGeneration;
    connectionsRequest = true;
    for (const id of ["github-fields", "connection-fields", "github-save", "connection-save"]) $(id).disabled = true;
    try {
      const next = await request("/api/connections");
      if (!current(generation) || page !== "settings") return;
      connections = next;
      const labels = { environment: "环境变量", custom: "Web 自定义", disabled: "已禁用" };
      for (const [kind, prefix] of [["github", "github"], ["checker", "connection"]]) {
        const value = next[kind];
        text(`${prefix}-source`, `当前来源：${labels[value.source]} · ${value.configured ? "已配置" : "未配置"}`);
        $(`${prefix}-mode`).value = value.source;
        text(`${prefix}-message`, "Web 自定义优先；禁用和恢复环境变量均需保存后生效。");
      }
      $("checker-api-url").value = next.checker.apiUrl || "";
      $("checker-web-url").value = next.checker.webUrl || "";
      connectionModeChanged("github");
      connectionModeChanged("checker");
    } catch (error) { if (current(generation)) notice(error.message, true); }
    finally {
      if (current(generation)) {
        connectionsRequest = false;
        for (const id of ["github-fields", "connection-fields", "github-save", "connection-save"]) $(id).disabled = false;
        controls.refresh();
      }
    }
  }

  async function loadMirrors() {
    const generation = authGeneration;
    try {
      const mirrors = await request("/api/mirrors");
      if (!current(generation) || page !== "settings") return;
      const value = settingsDirty ? $("download-mirror").value : state?.settings.downloadMirror || "";
      $("download-mirror").replaceChildren(new Option("直连", ""), new Option("自动选择", "auto"), ...mirrors.filter(mirror => mirror.prefix).map(mirror => new Option(`${mirror.name}${mirror.isDefault ? " · 默认候选" : ""}`, mirror.name)));
      $("download-mirror").value = value;
      controls.refresh();
    } catch (error) { if (current(generation)) text("mirror-message", error.message); }
  }

  async function loadFeatures() {
    if (featureRequest) return;
    const generation = authGeneration;
    featureRequest = true;
    $("features-refresh").disabled = true;
    text("features-message", "正在读取学习记录…");
    $("features-message").hidden = false;
    try {
      const next = await request("/api/features");
      if (!authenticated || generation !== authGeneration) return;
      featureData = next;
      renderFeatures();
    } catch (error) { if (current(generation)) text("features-message", `${error.message}${featureData ? " 下方保留上次读取的记录。" : ""}`); }
    finally { if (current(generation)) { featureRequest = false; $("features-refresh").disabled = false; } }
  }

  function renderFeatures() {
    if (!featureData) return;
    const query = $("feature-search").value.trim().toLocaleLowerCase();
    const records = featureData.filter(entry => `${entry.fullName} ${entry.knownSubPaths.join(" ")}`.toLocaleLowerCase().includes(query));
    const fragment = document.createDocumentFragment();
    for (const entry of records) {
      const row = make("tr");
      const name = make("td", "", entry.fullName);
      name.append(make("small", "muted", `${repoCategoryLabel(entry.category)} · ${entry.isReliable ? "稳定来源" : "继续观察"}`));
      const paths = make("td");
      const list = make("ul", "path-list");
      for (const path of entry.knownSubPaths) list.append(make("li", "", path));
      paths.append(list);
      row.append(name, paths, make("td", "", entry.totalNodes), make("td", "", entry.analysisCount), make("td", "", formatDate(entry.lastAnalyzed)));
      fragment.append(row);
    }
    $("feature-rows").replaceChildren(fragment);
    text("features-message", records.length ? `共 ${records.length} 条学习记录。` : query ? "没有匹配的记录，请换一个关键词。" : "尚无学习记录。成功识别订阅后，会在此保留可复用路径。");
    $("features-message").hidden = false;
  }

  function renderCheckerConnection() {
    const configured = state.checker.configured;
    if (!configured) checkerConnection = null;
    text("checker-connection", !configured ? "API 尚未配置" : checkerConnection === true ? "检测器已连接" : checkerConnection === false ? "检测器读取异常" : "API 已配置 · 待读取验证");
    $("checker-connection").classList.toggle("is-ready", configured && checkerConnection === true);
    $("checker-unconfigured").hidden = configured;
    $("checker-controls").hidden = !configured;
    const webUrl = safeExternalUrl(state.checker.webUrl);
    $("checker-web").hidden = !webUrl;
    if (webUrl) $("checker-web").href = webUrl;
    const feed = new URL(state.feedPath, location.origin);
    if (feed.origin !== location.origin) return;
    $("feed-url").value = feed.href;
    $("open-feed").href = feed.href;
  }

  async function loadCheckerConfig(generation) {
    const next = await request("/api/checker/config");
    if (!current(generation) || page !== "checker") return;
    checkerConfig = next;
    const fields = document.createDocumentFragment();
    for (const field of next.fields) {
      const group = make("div", "config-field");
      const label = make("label", "", field.label);
      const control = make(["boolean", "choice"].includes(field.kind) ? "select" : field.kind === "list" ? "textarea" : "input");
      control.id = `checker-field-${field.key}`;
      label.htmlFor = control.id;
      control.dataset.configKey = field.key;
      if (field.kind === "boolean") control.replaceChildren(new Option("未设置", ""), new Option("启用", "true"), new Option("关闭", "false"));
      else if (field.kind === "choice") control.replaceChildren(new Option("未设置", ""), ...field.choices.map(value => new Option(value, value)));
      else if (field.kind === "list") control.rows = 3;
      else {
        control.type = ["integer", "number"].includes(field.kind) ? "number" : field.kind === "url" ? "url" : "text";
        if (field.min != null) control.min = field.min;
        if (field.max != null) control.max = field.max;
        if (field.kind === "number") control.step = "any";
      }
      control.autocomplete = "off";
      control.value = field.hidden || field.value == null ? "" : Array.isArray(field.value) ? field.value.join("\n") : String(field.value);
      control.dataset.initialValue = control.value;
      if (field.hidden) control.placeholder = "已设置私密值 · 留空保留";
      label.append(control, make("small", "", field.hidden ? `${field.key} · 原值不回显，留空不修改` : field.key));
      group.append(label);
      if (field.hidden) {
        const clearLabel = make("label", "inline-check");
        const clear = make("input");
        clear.type = "checkbox";
        clear.id = `checker-clear-${field.key}`;
        clear.addEventListener("change", () => { control.disabled = clear.checked; if (clear.checked) control.value = ""; });
        clearLabel.append(clear, make("span", "", "清除此参数（不保留原值）"));
        group.append(clearLabel);
      }
      fields.append(group);
    }
    $("checker-config-fields").replaceChildren(fields);
    $("checker-config-form").hidden = $("checker-sources-form").hidden = false;
    text("checker-source-count", `检测器现有 ${next.sources.localCount} 条直接来源、${next.sources.remoteCount} 条远程来源清单。追加会保留这些来源。`);
    text("checker-config-message", "已读取配置。仅保存改动字段；若原管理台已修改配置，请重新读取后编辑。");
    controls.refresh();
  }

  async function readChecker() {
    if (checkerRequest || !state?.checker.configured) return;
    const generation = authGeneration;
    checkerRequest = true;
    $("checker-refresh").disabled = true;
    text("checker-message", "正在读取检测器状态与结果…");
    const responses = await Promise.allSettled([request("/api/checker/status"), request("/api/checker/results")]);
    if (!authenticated || generation !== authGeneration) {
      return;
    }
    const targets = ["checker-status-output", "checker-results-output"];
    const names = ["检测状态", "检测结果"];
    const errors = [];
    responses.forEach((result, index) => {
      if (result.status === "fulfilled") {
        text(targets[index], JSON.stringify(result.value, null, 2));
        if (index === 0) checkerStatus = result.value;
        else checkerResults = result.value;
      }
      else errors.push(`${names[index]}：${result.reason.message}`);
    });
    text("checker-message", errors.length ? `${errors.join(" ")} 已显示的数据保留，不代表本次读取成功。` : `已读取检测器实时返回 · ${formatDate(new Date().toISOString(), true)}`);
    checkerConnection = errors.length === 0;
    renderCheckerConnection();
    renderCheckerData();
    checkerRequest = false;
    $("checker-refresh").disabled = false;
  }

  async function pollCheckerStatus() {
    if (checkerRequest || !state?.checker.configured) return;
    const generation = authGeneration;
    checkerRequest = true;
    let finished = false;
    try {
      const next = await request("/api/checker/status");
      if (!authenticated || generation !== authGeneration) return;
      checkerStatus = next;
      text("checker-status-output", JSON.stringify(checkerStatus, null, 2));
      checkerConnection = true;
      finished = !checkerStatus.checking;
      renderCheckerConnection();
      renderCheckerData();
      text("checker-message", `检测状态已更新 · ${formatDate(new Date().toISOString(), true)}。结果区保留最近完成的一轮。`);
    } catch (error) {
      if (!current(generation)) return;
      checkerConnection = false;
      renderCheckerConnection();
      text("checker-message", `${error.message} 当前检测进度未确认；下方保留已读取的数据。`);
    } finally { if (current(generation)) checkerRequest = false; }
    if (finished) await readChecker();
  }

  function renderCheckerData() {
    if (!checkerStatus) $("checker-stats").replaceChildren();
    if (checkerStatus) {
      const stats = document.createDocumentFragment();
      const pipeline = checkerStatus.pipeline;
      const values = [["检测任务", checkerStatus.checking ? "正在检测" : "当前空闲"]];
      if (pipeline) {
        values.push(["节点总数", pipeline.total], ["连通性完成", pipeline.aliveDone], ["连通性通过", pipeline.alivePass], ["媒体处理完成", pipeline.mediaDone], ["筛选通过", pipeline.filterPass]);
        if (checkerStatus.hasSpeedTest) values.push(["测速完成", pipeline.speedDone], ["测速通过", pipeline.speedPass]);
      } else values.push(["阶段代码", checkerStatus.phase], ["节点总数", checkerStatus.proxyCount], ["可用节点", checkerStatus.available], ["已完成检测", checkerStatus.progress]);
      for (const [label, value] of values) {
        const item = make("div");
        item.append(make("dt", "", label), make("dd", "", value ?? "—"));
        stats.append(item);
      }
      $("checker-stats").replaceChildren(stats);
    }
    $("checker-run").disabled = checkerRunRequest || Boolean(checkerStatus?.checking);
    $("checker-stop").disabled = !checkerStatus?.checking || Boolean($("checker-stop").dataset.busyLabel);
    text("checker-run", checkerRunRequest ? "正在提交…" : checkerStatus?.checking ? "检测进行中…" : "开始一次检测");
    if (!checkerResults) {
      $("node-rows").replaceChildren();
      $("node-source-counts").replaceChildren();
      $("node-detail").hidden = $("node-pagination").hidden = true;
      $("node-empty").hidden = false;
      text("node-empty", "读取检测器后，这里会显示最近一次完成的节点结果。");
      text("checker-result-time", "尚未读取检测结果");
      return;
    }
    if (!Array.isArray(checkerResults.nodes)) {
      text("node-empty", "检测器返回的结果格式无法识别，请查看原始 JSON 并确认 subs-check 版本。");
      $("node-empty").hidden = false;
      return;
    }
    text("checker-result-time", `最近完成于 ${formatDate(checkerResults.checkedAt)}`);
    const visible = filterCheckerNodes(checkerResults.nodes, $("node-search").value, $("node-sort").value, $("node-positive-speed").checked);
    $("node-source-counts").replaceChildren(...retainedSources(checkerResults.nodes).map(source => {
      const item = make("li");
      const repo = sourceRepository(source.tag, [...state.repositories, ...state.favorites]);
      item.append(repo ? externalLink(`${repo.fullName} ↗`, repo.url) : make("span", "", source.tag));
      item.append(make("span", "", ` · ${source.count} 个已保留节点`));
      const speed = value => Number(value.toFixed(1)).toLocaleString("zh-CN");
      item.append(make("small", "", source.speedSamples
        ? `${source.speedSamples} 个正速度样本 · 均速 ${speed(source.averageSpeed)} KB/s · 最快 ${speed(source.maxSpeed)} KB/s`
        : `${checkerResults.speedTest ? "未测得正速度" : "本轮未测速"} · 暂无均速与最快速度`));
      return item;
    }));
    text("checker-node-summary", `${visible.length} / ${checkerResults.nodes.length} 个节点 · ${checkerResults.speedTest ? "本轮已启用测速" : "本轮未启用测速"} · ${checkerResults.mediaCheck ? "本轮已启用媒体检测" : "本轮未启用媒体检测"}`);
    const currentPage = checkerPage(visible, nodePage);
    nodePage = currentPage.index;
    $("node-pagination").hidden = currentPage.pageCount <= 1;
    text("node-page-label", `第 ${nodePage + 1} / ${currentPage.pageCount} 页 · 每页最多 100 条`);
    $("node-previous").disabled = nodePage === 0;
    $("node-next").disabled = nodePage + 1 === currentPage.pageCount;
    const signature = JSON.stringify([currentPage.items, selectedNode, checkerResults.speedTest, checkerResults.mediaCheck]);
    if (nodeTableSignature === signature) return;
    nodeTableSignature = signature;
    const fragment = document.createDocumentFragment();
    for (const node of currentPage.items) {
      const key = `${node.name}|${node.server}|${node.port}`;
      const row = make("tr", selectedNode === key ? "is-selected" : "");
      const name = make("td");
      const button = make("button", "repo-name-button", node.name || node.baseName || "未命名节点");
      button.type = "button";
      button.setAttribute("aria-controls", "node-detail");
      button.setAttribute("aria-pressed", String(selectedNode === key));
      button.addEventListener("click", () => {
        selectedNode = key;
        renderCheckerData();
        $("node-detail-title").focus({ preventScroll: true });
        $("node-detail").scrollIntoView({ block: "nearest", behavior: "instant" });
      });
      name.append(button);
      const media = make("td");
      const tags = make("div", "media-tags");
      if (node.media?.length) {
        for (const tag of node.media) tags.append(make("span", "", `${tag.platform}: ${tag.tag}`));
      } else tags.append(make("span", "", checkerResults.mediaCheck ? "无媒体检测记录" : "未检测"));
      media.append(tags);
      row.append(name, make("td", "", node.type || "—"), make("td", "", node.country || "未知"), make("td", "", node.speed > 0 ? `${Number(node.speed).toLocaleString("zh-CN")} KB/s` : "未测得速度"), media);
      fragment.append(row);
    }
    $("node-rows").replaceChildren(fragment);
    $("node-empty").hidden = visible.length > 0;
    text("node-empty", checkerResults.nodes.length ? "没有匹配的节点，请修改筛选条件。" : "本轮检测没有保留节点，请查看检测器状态与配置。");
    const selected = checkerResults.nodes.find(node => `${node.name}|${node.server}|${node.port}` === selectedNode);
    $("node-detail").hidden = !selected;
    if (selected) {
      const title = make("h3", "", selected.name || selected.baseName || "节点详情");
      title.id = "node-detail-title";
      title.tabIndex = -1;
      const details = make("dl", "detail-stats");
      for (const [label, value] of [["服务器", selected.server], ["端口", selected.port], ["网络", selected.network], ["TLS", selected.tls ? "启用" : "未启用"], ["SNI", selected.sni || "—"], ["UDP", selected.udp ? "支持" : "未标记支持"], ["Reality", selected.reality ? "启用" : "未启用"], ["来源标签", selected.subTag || "—"], ["IP 风险", selected.ipRisk || "未检测"], ...(selected.details || []).map(item => [item.k, item.v])]) {
        const item = make("div");
        item.append(make("dt", "", label), make("dd", "", value || "—"));
        details.append(item);
      }
      $("node-detail").replaceChildren(title, details);
    }
  }

  $("login-form").addEventListener("submit", async event => {
    event.preventDefault();
    $("login-button").disabled = true;
    text("login-button", "正在验证…");
    $("login-error").hidden = true;
    try {
      const preLoginSession = await request("/api/session");
      csrfToken = preLoginSession.csrfToken;
      await request("/api/login", { method: "POST", body: { password: $("password").value } });
      $("password").value = "";
      const session = await request("/api/session");
      csrfToken = session.csrfToken;
      if (!session.authenticated) throw new Error("登录会话未建立，请检查浏览器是否允许同源 Cookie。");
      await enterWorkspace();
    } catch (error) {
      text("login-error", error.message);
      $("login-error").hidden = false;
      $("password").value = "";
      $("password").focus();
    } finally {
      $("login-button").disabled = false;
      text("login-button", "进入工作台");
    }
  });

  $("logout-button").addEventListener("click", async () => {
    $("logout-button").disabled = true;
    try {
      await request("/api/logout", { method: "POST" });
      showLogin();
      await getSession();
    } catch (error) { notice(error.message, true); }
    finally { $("logout-button").disabled = false; }
  });

  $("refresh-button").addEventListener("click", async () => {
    const generation = authGeneration;
    runRequest = true;
    renderState();
    try {
      await request("/api/refresh", { method: "POST" });
      if (!current(generation)) return;
      notice("发现任务已提交。后台完成后，仓库列表会自动更新。");
      await loadState();
    } catch (error) { if (current(generation)) notice(error.message, true); }
    finally { if (current(generation)) { runRequest = false; if (state) renderState(); } }
  });

  $("cancel-button").addEventListener("click", async () => {
    const generation = authGeneration;
    cancelRequest = true;
    renderState();
    try {
      await request("/api/cancel", { method: "POST" });
      if (!current(generation)) return;
      notice("已请求取消，正在等待后台收尾；上次有效结果保持不变。");
      await loadState();
    } catch (error) { if (current(generation)) notice(error.message, true); }
    finally { if (current(generation)) { cancelRequest = false; if (state) renderState(); } }
  });

  $("settings-form").addEventListener("input", () => {
    settingsDirty = true;
    $("settings-save").disabled = false;
    text("settings-save-state", "有未保存的修改");
    text("settings-message", "");
  });

  $("settings-form").addEventListener("submit", async event => {
    event.preventDefault();
    if (!$("settings-form").reportValidity()) return;
    const generation = authGeneration;
    const settings = Object.fromEntries(Object.entries(settingsFields).map(([key, id]) => [key, $(id).type === "checkbox" ? $(id).checked : $(id).type === "number" ? Number($(id).value) : $(id).value]));
    $("settings-fields").disabled = true;
    $("settings-save").disabled = true;
    text("settings-save", "正在保存…");
    try {
      await request("/api/settings", { method: "PUT", body: settings });
      if (!current(generation)) return;
      settingsDirty = false;
      text("settings-message", "设置已保存；当前进行中的任务不受影响。");
      text("settings-save-state", "设置保存在服务端");
      await loadState();
    } catch (error) { if (current(generation)) text("settings-message", error.message); }
    finally {
      if (current(generation)) {
        $("settings-fields").disabled = false;
        $("settings-save").disabled = !settingsDirty;
        text("settings-save", "保存设置");
      }
    }
  });

  $("export-button").addEventListener("click", async () => {
    const generation = authGeneration;
    const { repos, visible } = repositoryView();
    const scope = $("export-scope").value;
    const selected = repositoriesForScope(repos, visible, selectedRepos, scope);
    exporting = true;
    renderRepositories();
    const format = $("export-format").value;
    try {
      const blob = await request("/api/export", { method: "POST", body: { format, fullNames: selected.map(repo => repo.fullName) }, raw: true, timeout: 120000 });
      if (!current(generation)) return;
      download(blob, `proxynodehub-${scope}-${format}.${format === "json" ? "json" : "txt"}`);
      notice(`已将 ${selected.length} 个仓库的导出文件交给浏览器下载。`);
    } catch (error) { if (current(generation)) notice(error.message, true); }
    finally { if (current(generation)) { exporting = false; if (state && ["discover", "favorites"].includes(page)) renderRepositories(); } }
  });

  $("copy-feed").addEventListener("click", async () => {
    $("feed-url").focus();
    $("feed-url").select();
    if (!navigator.clipboard || !window.isSecureContext) {
      notice("地址已选中。当前为 HTTP 内网连接，请使用系统复制操作。");
      return;
    }
    try { await navigator.clipboard.writeText($("feed-url").value); notice("订阅来源清单地址已复制。"); }
    catch { notice("浏览器未允许复制。地址已选中，请使用系统复制操作。", true); }
  });

  $("checker-run").addEventListener("click", async () => {
    const generation = authGeneration;
    checkerRunRequest = true;
    renderCheckerData();
    try {
      await request("/api/checker/run", { method: "POST" });
      if (!current(generation)) return;
      notice("检测请求已提交给 subs-check。请读取状态查看实际进展。");
      await readChecker();
    } catch (error) { if (current(generation)) notice(error.message, true); }
    finally { if (current(generation)) { checkerRunRequest = false; renderCheckerData(); } }
  });

  for (const [formId, kind, prefix] of [["github-form", "github", "github"], ["connection-form", "checker", "connection"]]) {
    $(formId).addEventListener("submit", async event => {
      event.preventDefault();
      if (!$(formId).reportValidity()) return;
      const mode = $(`${prefix}-mode`).value;
      if (mode !== "custom" && !await confirmChange(mode === "disabled" ? "禁用会停止使用此连接的自定义与环境配置。确定保存？" : "恢复环境变量将移除 Web 自定义覆盖。确定保存？")) return;
      const body = kind === "github" ? { mode, token: mode === "custom" ? $("github-token").value : undefined }
        : { mode, ...(mode === "custom" ? { apiUrl: $("checker-api-url").value, webUrl: $("checker-web-url").value, apiKey: $("checker-api-key").value } : {}) };
      action($(`${prefix}-save`), async generation => {
        $(`${prefix}-fields`).disabled = true;
        try {
          await request(`/api/connections/${kind}`, { method: "PUT", body });
          if (!current(generation)) return;
          $(kind === "github" ? "github-token" : "checker-api-key").value = "";
          if (kind === "checker") {
            checkerConnection = checkerStatus = checkerResults = null;
            nodeTableSignature = "";
            text("checker-status-output", "尚未读取");
            text("checker-results-output", "尚未读取");
            text("checker-logs", "尚未读取");
            text("checker-version", "");
            renderCheckerData();
          }
          await loadState();
          if (!current(generation)) return;
          if (page === "settings") await loadConnections();
          if (current(generation)) text(`${prefix}-message`, "配置已保存；下次操作使用新配置，无需重建容器。");
        } finally {
          if (current(generation)) {
            $(kind === "github" ? "github-token" : "checker-api-key").value = "";
            $(`${prefix}-fields`).disabled = false;
          }
        }
      }, `${prefix}-message`);
    });
    $(`${prefix}-mode`).addEventListener("change", () => connectionModeChanged(kind));
  }
  $("checker-api-url").addEventListener("input", () => connectionModeChanged("checker"));

  $("explore-button").addEventListener("click", () => action($("explore-button"), async generation => {
    await request("/api/explore", { method: "POST" });
    if (!current(generation)) return;
    notice("已提交新来源探索，使用已保存的排除规则；新来源会加入现有结果，其余来源保留。");
    await loadState();
  }));

  $("mirror-test").addEventListener("click", () => action($("mirror-test"), async generation => {
    const results = await request("/api/mirrors/test", { method: "POST", timeout: 120000 });
    if (current(generation)) text("mirror-message", results.map(result => `${result.name}：${result.latencyMs == null || result.latencyMs < 0 ? "测试失败" : `${result.latencyMs} ms`}`).join("；"));
  }, "mirror-message"));

  $("select-filtered").addEventListener("change", () => {
    for (const repo of repositoryView().visible) {
      if ($("select-filtered").checked) selectedRepos.add(repo.fullName);
      else selectedRepos.delete(repo.fullName);
    }
    renderRepositories();
  });
  $("clear-selection").addEventListener("click", () => { selectedRepos.clear(); renderRepositories(); });
  for (const [id, favorite] of [["bulk-favorite", true], ["bulk-unfavorite", false]]) $(id).addEventListener("click", async () => {
    const { repos, visible } = repositoryView();
    const fullNames = repositoriesForScope(repos, visible, selectedRepos, "selected").map(repo => repo.fullName);
    if (!favorite && !await confirmChange(`取消收藏选中的 ${fullNames.length} 个仓库？已发现结果不会删除。`)) return;
    action($(id), async generation => {
      $("bulk-fields").disabled = true;
      try {
        await request("/api/favorites/batch", { method: "PUT", body: { fullNames, favorite } });
        if (!current(generation)) return;
        notice(`已${favorite ? "收藏" : "取消收藏"} ${fullNames.length} 个仓库。`);
        await loadState();
      } finally { if (current(generation)) $("bulk-fields").disabled = false; }
    });
  });
  $("bulk-recheck").addEventListener("click", () => action($("bulk-recheck"), async generation => {
    const { repos, visible } = repositoryView();
    const fullNames = repositoriesForScope(repos, visible, selectedRepos, "selected").map(repo => repo.fullName);
    await request("/api/recheck", { method: "POST", body: { fullNames } });
    if (!current(generation)) return;
    notice(`已提交 ${fullNames.length} 个仓库重检；其他来源保持不变。`);
    await loadState();
  }));
  for (const id of ["copy-links", "copy-names"]) $(id).addEventListener("click", () => action($(id), async generation => {
    const { repos, visible } = repositoryView();
    const selected = repositoriesForScope(repos, visible, selectedRepos, "selected");
    const values = id === "copy-names" ? selected.map(repo => repo.fullName) : selected.flatMap(repo => repo.links.map(link => safeExternalUrl(link.url)).filter(Boolean));
    await copyText([...new Set(values)].join("\n"), generation);
  }));
  $("manual-copy-close").addEventListener("click", () => { $("manual-copy-text").value = ""; $("manual-copy").hidden = true; });

  $("logs-refresh").addEventListener("click", () => action($("logs-refresh"), loadLogs));
  $("log-run").addEventListener("change", () => {
    logData = null;
    historySignature = "";
    renderHistory();
    action($("logs-refresh"), loadLogs);
  });
  $("logs-download").addEventListener("click", () => action($("logs-download"), async generation => {
    const blob = await request(logPath(true), { raw: true });
    if (current(generation)) download(blob, "proxynodehub-discovery.log");
  }));
  $("logs-clear").addEventListener("click", async () => {
    if (!await confirmChange("清空全部发现日志？此操作不可恢复，但不会删除运行历史、发现结果或改变调度。")) return;
    action($("logs-clear"), async generation => {
      await request("/api/logs", { method: "DELETE" });
      if (!current(generation)) return;
      logRequest += 1;
      logData = [];
      await loadState();
      if (current(generation)) notice("发现日志已清空，运行历史与调度保留。");
    });
  });
  $("search-history-refresh").addEventListener("click", () => action($("search-history-refresh"), loadSearchHistory, "search-history-message"));
  $("search-history-clear").addEventListener("click", async () => {
    if (!await confirmChange("清空全部搜索记忆？探索将不再跳过这些近期搜索过的仓库。")) return;
    action($("search-history-clear"), async generation => {
      await request("/api/search-history", { method: "DELETE" });
      if (current(generation)) await loadSearchHistory(generation);
    }, "search-history-message");
  });

  $("checker-stop").addEventListener("click", async () => {
    if (!await confirmChange("请求 subs-check 停止当前检测？已完成的结果会保留。")) return;
    action($("checker-stop"), async generation => {
      await request("/api/checker/stop", { method: "POST" });
      if (!current(generation)) return;
      notice("停止请求已提交，请等待检测器确认收尾。");
      await readChecker();
    });
  });
  $("checker-version-read").addEventListener("click", () => action($("checker-version-read"), async generation => {
    const value = await request("/api/checker/version");
    if (current(generation)) text("checker-version", `检测器版本：${value.version}`);
  }));
  $("checker-logs-read").addEventListener("click", () => action($("checker-logs-read"), async generation => {
    const value = await request("/api/checker/logs");
    if (current(generation)) text("checker-logs", value.logs.length ? value.logs.join("\n") : "检测器没有可显示的日志。");
  }));
  $("checker-download").addEventListener("click", () => action($("checker-download"), async generation => {
    const format = $("checker-download-format").value;
    const blob = await request(`/api/checker/download?format=${encodeURIComponent(format)}`, { raw: true, timeout: 120000 });
    if (current(generation)) download(blob, `subs-check-${format}.${format === "base64" ? "txt" : "yaml"}`);
  }));
  $("checker-config-read").addEventListener("click", () => action($("checker-config-read"), loadCheckerConfig, "checker-config-message"));
  $("checker-config-form").addEventListener("submit", event => {
    event.preventDefault();
    if (!checkerConfig || !$("checker-config-form").reportValidity()) return;
    action($("checker-config-save"), async generation => {
      const values = {};
      for (const field of checkerConfig.fields) {
        const control = $(`checker-field-${field.key}`);
        if (field.hidden && $(`checker-clear-${field.key}`).checked) { values[field.key] = ""; continue; }
        if (control.value === control.dataset.initialValue || (field.hidden && !control.value)) continue;
        if (["boolean", "integer", "number", "choice"].includes(field.kind) && control.value === "") throw new Error(`${field.label} 不能清空；请填入有效值。`);
        values[field.key] = field.kind === "boolean" ? control.value === "true" : ["integer", "number"].includes(field.kind) ? Number(control.value) : field.kind === "list" ? control.value.split(/\r?\n/).map(line => line.trim()).filter(Boolean) : control.value;
      }
      if (!Object.keys(values).length) { text("checker-config-message", "没有改动参数。"); return; }
      if (!await confirmChange(`将 ${Object.keys(values).length} 项改动写入实际检测器配置？不会自动开始检测。`, generation)) { if (current(generation)) text("checker-config-message", "未保存改动。"); return; }
      $("checker-config-fields").disabled = true;
      try {
        await request("/api/checker/config", { method: "PUT", body: { revision: checkerConfig.revision, values } });
        if (!current(generation)) return;
        await loadCheckerConfig(generation);
        if (current(generation)) text("checker-config-message", "参数已保存；未改动配置与已有来源保持不变。");
      } finally { if (current(generation)) $("checker-config-fields").disabled = false; }
    }, "checker-config-message");
  });
  $("checker-sources-form").addEventListener("submit", event => {
    event.preventDefault();
    if (!checkerConfig) return;
    action($("checker-sources-save"), async generation => {
      const body = { revision: checkerConfig.revision, current: $("checker-source-current").checked, favorites: $("checker-source-favorites").checked, manualUrls: $("checker-manual-urls").value.split(/\r?\n/).map(line => line.trim()).filter(Boolean) };
      if (!body.current && !body.favorites && !body.manualUrls.length) throw new Error("请选中来源集合或输入手动订阅 URL。");
      if (!await confirmChange("将这些订阅追加到实际检测器？已有来源保持不变，不会自动开始检测。", generation)) { if (current(generation)) text("checker-config-message", "未追加来源。"); return; }
      await request("/api/checker/sources", { method: "POST", body });
      if (!current(generation)) return;
      $("checker-manual-urls").value = "";
      $("checker-source-current").checked = $("checker-source-favorites").checked = false;
      await loadCheckerConfig(generation);
      if (current(generation)) text("checker-config-message", "来源已追加到检测器；公开来源清单未加入手动 URL。");
    }, "checker-config-message");
  });

  $("repo-search").addEventListener("input", () => { if (state) renderRepositories(); });
  $("repo-sort").addEventListener("change", () => { if (state) renderRepositories(); });
  for (const id of ["min-commits", "min-age", "max-inactive"]) $(id).addEventListener("input", () => { if (state) renderRepositories(); });
  for (const id of ["repo-direction", "repo-extra-columns", "export-scope"]) $(id).addEventListener("change", () => { if (state) renderRepositories(); });
  $("clear-filter").addEventListener("click", () => { for (const id of ["repo-search", "min-commits", "min-age", "max-inactive"]) $(id).value = ""; saveViewPreferences(); renderRepositories(); $("repo-search").focus(); });
  $("feature-search").addEventListener("input", renderFeatures);
  $("node-search").addEventListener("input", () => { nodePage = 0; renderCheckerData(); });
  for (const id of ["node-sort", "node-positive-speed"]) $(id).addEventListener("change", () => { nodePage = 0; renderCheckerData(); });
  $("node-previous").addEventListener("click", () => { nodePage -= 1; renderCheckerData(); });
  $("node-next").addEventListener("click", () => { nodePage += 1; renderCheckerData(); });
  $("features-refresh").addEventListener("click", loadFeatures);
  $("checker-refresh").addEventListener("click", readChecker);
  $("session-retry").addEventListener("click", getSession);
  $("state-retry").addEventListener("click", loadState);
  $("dismiss-notice").addEventListener("click", () => { $("action-notice").hidden = true; });
  window.addEventListener("hashchange", navigate);
  window.addEventListener("pagehide", () => { controls.dismiss(); clearPrivateInputs(); });
  for (const id of Object.keys(viewPreferenceRules)) $(id).addEventListener($(id).type === "number" ? "input" : "change", saveViewPreferences);
  restoreViewPreferences();
  controls.refresh();
  getSession();
}
