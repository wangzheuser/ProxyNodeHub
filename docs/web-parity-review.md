# Web 组件与完整能力复核（2026-10-07）

> 本文是2026-10-07的审查快照。后续 Docker Hub 工作流与订阅解析修复以当前 [README](../README.md#github-actions-与镜像发布) 和源码为准；下文“未接入 Docker Hub”等发布状态不代表后续版本。功能差异仍须按各自证据判断。

## 结论与部署决定

**Web 与 Windows 客户端并非全部功能相同。** 前一轮补齐了主要管理能力，但“已补齐该轮清单”不等于“全功能等价”。本轮按用户最新要求，仅在全部相同时重新部署，因此没有替换正式容器，也没有提交、推送或触发 Actions。

本轮自设计组件已实现：暖纸底、墨色文字、印章红下拉选单、复选框、折叠标记、进度条、确认框及内联校验反馈。保留语义 HTML、原表单约束与单一字段值；隐藏 select 只保存现有表单值，不显示原生下拉。确认框使用自定义样式，浏览器负责焦点隔离。没有新增 UI 库。

按用户截图反馈，下拉箭头改为自绘并统一16px右侧内边距，长文本不挤压箭头；滚动时菜单跟随触发器定位。

## 全功能对照

“已覆盖”表示用户可完成同类任务，不表示返回结果、允许值和副作用逐字相同。

| 能力 | 当前结论 | 主要证据 |
|---|---|---|
| GitHub Token 设置/替换/禁用 | 已覆盖；Web另支持恢复环境，不回显旧密钥 | `web/ConnectionStore.cs:77`、`web/Api.cs:27` |
| 检测器 URL/API Key 配置 | 已覆盖，服务端保存，下一次请求生效 | `web/ConnectionStore.cs` |
| 搜索、评分、订阅识别与学习 | 共用Core，但Web只原子发布有效公开来源，客户端流式显示全部候选 | `core/DiscoveryEngine.cs:15`、`web/DiscoveryWorker.cs:238`、`src/MainForm.cs:2089` |
| 取消发现/失败结果 | Web保留上轮；客户端可留下本轮部分候选，不等价 | `web/DiscoveryWorker.cs:131`、`src/MainForm.cs:2298` |
| 搜索模式与历史排除 | Web提供分析并发、单独探索；客户端搜索并发及每轮排除规则不同 | `web/DiscoveryWorker.cs:191`、`src/MainForm.cs:2097` |
| 文本与数值筛选、普通排序 | 已覆盖；Web收藏也用数值过滤，未更新天数的边界与客户端不同 | `web/wwwroot/app.js:12`、`src/MainForm.cs:2347` |
| 单个/批量收藏、取消收藏 | 已覆盖；Web收藏**总数最多200**，客户端无同等上限 | `web/StateStore.cs:121`、`src/Settings.cs:163` |
| 单个/批量重检 | 已覆盖；Web零有效链接或失败时保留旧来源，客户端原地更新候选状态 | `web/DiscoveryWorker.cs:238`、`web/StateStore.cs:145` |
| 仓库名/订阅/单链接复制 | 已覆盖；HTTP或剪贴板拒绝时提供手动选择复制 | `web/wwwroot/app.js` 的 `copyText`、`renderDetail` |
| 打开仓库 | 单个已覆盖；缺客户端多选批量打开 | `src/MainForm.cs:1711`、`web/wwwroot/app.js` 的 `renderDetail` |
| 快捷键与面板开合 | 未逐项复制客户端Ctrl+F/C/L/D、F5与面板行为，保留浏览器快捷键 | `src/MainForm.cs:1091`、`web/wwwroot/app.js` |
| 链接/URI/Base64导出 | 已覆盖，Web增加范围/JSON；缺合并结果预览/直接复制，且失败策略更严格 | `src/ExportDialog.cs:106`、`web/Api.cs:82`、`web/DiscoveryApi.cs:23` |
| 镜像列表/固定/自动/测速 | 已覆盖；Web限制公开下载、禁止重定向，固定镜像失败不自动换源 | `core/GitHubService.cs:245` |
| 搜索记忆读取/清理 | 已覆盖；过期规则、续期和探索追加语义不同 | `src/SearchHistory.cs:73`、`web/StateStore.cs:183` |
| 发现日志与运行历史 | 已覆盖读取/清理/下载，但Web每轮仅末200条、最多20轮；不等于完整N天日志 | `web/DiscoveryWorker.cs:80`、`web/StateStore.cs:188`、`src/LogStore.cs:45` |
| 自动学习路径 | 共用Core；Web提供已学习路径查看 | `core/SubscriptionFinder.cs:31`、`web/Api.cs:39` |
| 自定义特征规则管理 | Web无导入/导出/删除/重置/AI文档管理；客户端规则也未接入发现核心 | `src/FeatureLibraryDialog.cs:302`、`src/CustomFeatureLibrary.cs` |
| 开始检测与进度 | 共用上游触发/状态协议；客户端启动本机进程，Web连接常驻服务 | `src/SpeedTestRunner.cs:363`、`web/Api.cs:57` |
| 停止检测 | 不等价：客户端杀进程树，Web仅请求取消当前流水线，不停止服务或后续调度 | `src/SpeedTestRunner.cs:320`、`web/CheckerApi.cs:15` |
| 常用检测参数 | 客户端24个表单键全部覆盖，Web27个；允许值和生效时机不同 | `src/SpeedTestSettingsDialog.cs:179`、`web/CheckerConfig.cs:16` |
| 完整YAML/高级参数/存储凭据 | 未集成；Web白名单外的cron、过滤、DNS等需原管理台，不计作本Web功能 | `src/SpeedTestSettingsDialog.cs:384`、`web/CheckerConfig.cs:16` |
| 当前/收藏/手动检测来源 | 类别覆盖但不等价：Web持久追加全量当前来源，客户端按筛选生成本轮会话；Web无选择性撤回/仅本轮替换 | `src/SpeedTestRunner.cs:210`、`src/MainForm.cs:920`、`web/CheckerApi.cs:26` |
| 按轮历史节点回灌 | Web未实现；共同的keep-days按天保留不能算作按轮替代 | `src/SpeedSourceDialog.cs:224`、`web/CheckerConfig.cs:34` |
| 节点详情、速度、媒体检测与筛选 | 同类能力已覆盖；Web增加来源统计、JSON与分页，0速度不误报离线 | `src/SpeedResultPanel.cs:137`、`web/wwwroot/app.js` 的 `renderCheckerData` |
| 仓库通过率/可用分/降权与恢复 | 未覆盖；当前上游缺少真实每来源失败分母，客户端指标也存在缺陷 | `src/SpeedTestStore.cs:126`、`src/MainForm.cs:974` |
| 检测日志 | Web手动读最近100行并脱敏；缺客户端实时追加、清屏等体验 | `src/SpeedTestRunner.cs:293`、`web/CheckerApi.cs:67` |
| 测速后持续订阅URL | Web未提供；文件下载或检测前来源清单均不等价于可续订URL | `src/SpeedTestPanel.cs:580`、`web/CheckerApi.cs:154` |
| 已生成检测产物下载 | Web支持Clash/Mihomo/Base64真实文件，是已覆盖的交付方式 | `web/CheckerApi.cs:154` |
| 内核版本/安装/升级/路径/回滚 | Web仅查看版本；不管理外部服务进程，客户端本机管理未覆盖 | `src/SubCheckKernel.cs:149`、`src/KernelDialog.cs:307`、`web/CheckerApi.cs:21` |
| 原检测器管理台入口 | 两端有；跳转不是本Web已集成高级管理的证明 | `src/SpeedTestPanel.cs:593`、`web/wwwroot/app.js` 的 `renderCheckerConnection` |
| 窗口状态、文件对话框、About | 属于桌面平台交互，不强行模拟成浏览器能力 | `src/MainForm.cs` |

Web额外的后台定时运行、关闭浏览器继续工作、认证、公开来源端点不能抵消以上缺口。

## 剩余缺口的设计方向

1. **来源与候选分离**：保留有效公开清单的严格边界，为运行候选另加状态投影；显示失败原因与部分进度。重检须区分“确认失效”与“网络未确认”，不能为对齐而把未验证链接公开。
2. **日志与收藏容量**：若要求客户端同等容量，先设数据卷预算、日志分页和按时轮转，再移除固定200条/20轮限制；当前页面应明确显示已有截断边界。
3. **本轮检测与持久配置分离**：先确定独立检测器是否支持会话来源/取消合同；没有上游原子会话接口时，不能用覆盖正式配置再恢复的方式伪装安全隔离。至少需明确来源查看、按标识删除、选中/筛选范围提交等合同。
4. **持续订阅与高级配置**：持续订阅需要可撤销、只读、限定产物的访问授权，不能匿名公开节点凭据；高级配置需字段所有权、敏感值只写和并发编辑策略。现有原管理台没有CAS，不能承诺跨管理台原子更新。
5. **纯交互缺口**：合并产物预览/复制、受浏览器弹窗规则约束的批量打开、可发现的非冲突快捷键，以及检测日志轮询/暂停可以独立补齐，不需要改变发现核心。
6. **不复制原版错误**：旧规则未接线、历史YAML缺少节点密钥、失败节点未计入分母、0速度被误报失败、success-rate百分比/比例单位错误，不能为了按钮相同而复制。按轮历史和真实通过率需完整产物及上游统计合同。

本轮以上仅完成复核与方案，未将它们标成已实现。若要把它们作为后续完整迁移验收，仍需真实实现及合同测试，而不是接受“近似替代”后宣称全部相同。

## GitHub Actions / Docker Hub

| 位置 | 当前事实 |
|---|---|
| 公开远端main | 核查时只有Windows .NET8构建/测试，尚无根Dockerfile或Docker发布任务 |
| 本地未提交 `.github/workflows/build.yml` | 已配置Core/Web回归、Windows构建和Linux amd64 Docker构建；HTTP及重启冒烟通过后只推GHCR |
| 本地Dockerfile | .NET10 Alpine多阶段构建，非root运行，含/live健康检查；与镜像仓库无绑定 |

本地GHCR策略：main/master发布latest和SHA；v*标签发布版本和SHA；PR不发布，其他分支手动运行也不发布。当前没有多架构构建配置。

**技术上可以推Docker Hub，当前没有接入。** 需要确定命名空间/仓库/可见性，配置Actions变量 `DOCKERHUB_USERNAME` 与 Secret `DOCKERHUB_TOKEN`（具有推送权限的Docker Hub PAT），在已有门禁之后登录、给已测试的 `proxynodehub:ci` 打Docker Hub标签并推送。可保留GHCR双发，不必重复构建或修改Dockerfile。随后须经授权提交并推送完整Core/Web/Dockerfile/测试变更。

没有读取凭据、设置Secret、调用发布API或触发远端Actions。远端Actions开关、Secret是否存在、Docker Hub仓库权限及真实runner推送均未验证。

一手资料：[远端工作流](https://github.com/zsj1024/ProxyNodeHub/blob/main/.github/workflows/build.yml)、[Docker Hub PAT](https://docs.docker.com/security/access-tokens/personal-access-tokens/)、[Docker多仓库发布](https://docs.docker.com/build/ci/github-actions/push-multi-registries/)。上游协议按已核查subs-check commit `3c320fd58aff5235e16218c050ec5b8ce587e233`；不代表正式检测器正在运行同一版本。

## 本轮验证边界

- Node语法、已有app.js自检通过。
- 合成API下1440px/375px浏览器实测通过：选择提交/取消、选项变更、勾选半选状态、禁用继承、动态镜像/参数、表单有效性、确认框焦点与恰好一次写入、切页/会话失效取消。
- 原有镜像目录失败/延迟回归通过；CSP保持原限制，无内联脚本或新依赖。
- 本机无dotnet且Docker daemon未运行，本轮未重跑.NET/真实HTTP服务/Windows GUI/容器构建；后端没有本轮代码变更，不能用此前发布门禁代替新版完整发布验证。
- 窄屏是浏览器视口模拟，不是真机验证；尚未实测Safari/Firefox与屏幕阅读器。
- 没有再次部署，正式服务仍保留前轮版本；本轮UI变更仅存在于工作树。
