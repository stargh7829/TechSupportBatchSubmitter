using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TechSupportBatchSubmitter.Core.Exceptions;
using TechSupportBatchSubmitter.Core.Interfaces;
using TechSupportBatchSubmitter.Core.Models;
using TechSupportBatchSubmitter.Core.Services;

namespace TechSupportBatchSubmitter.Wpf.Services;

public sealed class WebViewTicketPlatformClient : ITicketPlatformClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly WebView2 _webView;
    private readonly Uri _workbenchUri;
    private readonly Uri _supportPlatformUri;
    private readonly Dictionary<string, PlatformOption> _personCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlatformOption> _typeCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlatformOption> _systemCache = new(StringComparer.Ordinal);

    public WebViewTicketPlatformClient(WebView2 webView, AppSettings settings)
    {
        _webView = webView;
        _workbenchUri = settings.WorkbenchUri;
        _supportPlatformUri = settings.SupportPlatformUri;
    }

    public event EventHandler? SessionExpired;

    public async Task InitializeAsync(string userDataDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(userDataDirectory);
        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataDirectory);
        await _webView.EnsureCoreWebView2Async(environment);

        _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        _webView.CoreWebView2.Settings.IsStatusBarEnabled = true;
        _webView.CoreWebView2.ServerCertificateErrorDetected += (_, args) =>
        {
            if (InternalPlatformEndpointPolicy.IsCertificateBypassAllowed(args.RequestUri))
            {
                args.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
                return;
            }

            args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        };
        _webView.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? targetUri) &&
                targetUri is not null)
            {
                _webView.CoreWebView2.Navigate(targetUri.AbsoluteUri);
            }
        };
    }

    public void NavigateToWorkbench() =>
        _webView.CoreWebView2?.Navigate(_workbenchUri.AbsoluteUri);

    public void NavigateToSupportPlatform() =>
        _webView.CoreWebView2?.Navigate(_supportPlatformUri.AbsoluteUri);

    public async Task OpenSupportPlatformAsync()
    {
        if (_webView.CoreWebView2 is null)
        {
            return;
        }

        var clickedJson = await _webView.CoreWebView2.ExecuteScriptAsync(
            """
            (() => {
                const target = [...document.querySelectorAll("[onclick]")]
                    .find(element =>
                        String(element.getAttribute("onclick") || "")
                            .includes("proxylogin('JSZC'"));
                if (!target) return false;
                target.click();
                return true;
            })()
            """);
        if (!string.Equals(clickedJson, "true", StringComparison.OrdinalIgnoreCase))
        {
            NavigateToSupportPlatform();
        }
    }

    public async Task<PlatformSessionStatus> CheckSessionAsync(CancellationToken cancellationToken = default)
    {
        var script =
            """
            (async () => {
                const url = String(location.href || "");
                const body = String(document.body?.innerText || "");
                const isSupportPath = url.includes("/xzsw/") && !url.includes("/xzsw/login");
                const workbenchReady =
                    url.includes("172.18.75.21") &&
                    body.includes("你好！");
                const supportName = body.match(/公司\s*-\s*([^\s]+)/)?.[1] || "";
                const workbenchName = body.match(/你好！\s*([^\s]+)/)?.[1] || "";
                if (!isSupportPath) {
                    return {
                        ok: true,
                        data: {
                            isSupportPlatformReady: false,
                            isAuthenticated: workbenchReady,
                            displayName: supportName || workbenchName,
                            message: workbenchReady
                                ? "工作台已登录，请点击“技术支持”进入系统"
                                : "请在左侧登录页面完成登录"
                        }
                    };
                }

                try {
                    const response = await fetch("/xzsw/zcaseManager/listType.do", {
                        method: "POST",
                        cache: "no-store",
                        credentials: "same-origin"
                    });
                    const text = await response.text();
                    const loginResponse =
                        response.url?.includes("/login") ||
                        text.includes('"sessionstatus":"timeout"') ||
                        (text.includes("登录") && text.includes("password"));
                    const supportReady = response.ok && !loginResponse;
                    return {
                        ok: true,
                        data: {
                            isSupportPlatformReady: supportReady,
                            isAuthenticated: supportReady,
                            displayName: supportName || workbenchName,
                            message: supportReady
                                ? "技术支持系统已登录（接口核验）"
                                : "技术支持系统登录已失效"
                        }
                    };
                } catch (error) {
                    return {
                        ok: true,
                        data: {
                            isSupportPlatformReady: false,
                            isAuthenticated: false,
                            displayName: supportName || workbenchName,
                            message: "技术支持系统接口核验失败"
                        }
                    };
                }
            })()
            """;

        var data = await ExecuteAsync<PlatformSessionStatus>(script, cancellationToken);
        return data;
    }

    public async Task<ResolvedTicket> ResolveTicketAsync(
        TicketRow row,
        CancellationToken cancellationToken = default)
    {
        var discoverer = await ResolvePersonAsync(row.Discoverer, "0", cancellationToken);
        var applicant = await ResolvePersonAsync(row.Applicant, "0", cancellationToken);
        var assignee = await ResolvePersonAsync(row.Assignee, "1326", cancellationToken);
        var eventType = await ResolveDictionaryAsync(
            row.EventType,
            "/xzsw/zcaseManager/listType.do",
            _typeCache,
            "事件类型",
            cancellationToken);
        var system = await ResolveDictionaryAsync(
            row.SystemName,
            "/xzsw/zcaseManager/listSystem.do",
            _systemCache,
            "所属系统",
            cancellationToken);

        return new ResolvedTicket(row, discoverer, applicant, eventType, system, assignee);
    }

    public async Task<string> AllocateCaseIdAsync(CancellationToken cancellationToken = default)
    {
        var script =
            """
            (async () => {
                try {
                    const response = await fetch("/xzsw//zcaseManager/getCaseId.do", {
                        method: "GET",
                        cache: "no-store",
                        credentials: "same-origin"
                    });
                    const text = await response.text();
                    const timeout = detectSessionTimeout(text, response.url);
                    if (timeout) return timeout;
                    if (!response.ok) {
                        return { ok: false, kind: "protocol", error: `编号申请失败：HTTP ${response.status}` };
                    }
                    const documentValue = new DOMParser().parseFromString(text, "text/html");
                    const caseId = documentValue.getElementById("case_id")?.value?.trim() || "";
                    if (!/^\d+$/.test(caseId)) {
                        return { ok: false, kind: "protocol", error: "申请页未返回有效技术支持编号" };
                    }
                    return { ok: true, data: caseId };
                } catch (error) {
                    return { ok: false, kind: "network", error: String(error?.message || error) };
                }

                function detectSessionTimeout(text, url) {
                    if (url?.includes("/login") || text.includes('"sessionstatus":"timeout"')) {
                        return { ok: false, kind: "session", error: "技术支持系统登录已失效" };
                    }
                    return null;
                }
            })()
            """;

        return await ExecuteAsync<string>(script, cancellationToken);
    }

    public async Task<SaveTicketResult> SaveTicketAsync(
        ResolvedTicket ticket,
        string caseId,
        CancellationToken cancellationToken = default)
    {
        // 已在技术支持平台“事件申请”页实测：name=processingType，运维=1，运营=2。
        // 请勿猜测字段名或字段值；未知值必须终止提交。
        var processingTypeCode = ticket.Source.ProcessingType.Trim() switch
        {
            "运维" => "1",
            "运营" => "2",
            _ => throw new PlatformProtocolException(
                $"处理类型“{ticket.Source.ProcessingType}”未在平台事件申请页得到验证，已停止提交。")
        };
        var payload = new
        {
            case_id = caseId,
            saveFlag = "1",
            // 平台表单的实际字段：processingType（1=运维，2=运营）。
            processingType = processingTypeCode,
            case_title = ticket.Source.Title,
            proposer_name = ticket.Discoverer.Text,
            proposer_id = ticket.Discoverer.Id,
            case_creator_name = ticket.Applicant.Text,
            case_creator_id = ticket.Applicant.Id,
            type_id = ticket.EventType.Id,
            system = ticket.System.Id,
            module = string.Empty,
            assigned_to_name = ticket.Assignee.Text,
            assigned_to = ticket.Assignee.Id,
            case_description = ticket.Source.Description
        };
        var payloadJson = JsonSerializer.Serialize(payload);
        var script =
            $$"""
            (async () => {
                const payload = {{payloadJson}};
                try {
                    const body = new URLSearchParams(payload);
                    const response = await fetch("/xzsw/zcaseManager/saveCase.do", {
                        method: "POST",
                        cache: "no-store",
                        credentials: "same-origin",
                        headers: { "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8" },
                        body
                    });
                    const text = await response.text();
                    if (response.url?.includes("/login") || text.includes('"sessionstatus":"timeout"')) {
                        return { ok: false, kind: "session", error: "技术支持系统登录已失效" };
                    }
                    if (!response.ok) {
                        return { ok: false, kind: "unknown", error: `保存请求返回 HTTP ${response.status}` };
                    }
                    return {
                        ok: true,
                        data: {
                            requestAccepted: true,
                            responseText: text.slice(0, 500)
                        }
                    };
                } catch (error) {
                    return {
                        ok: false,
                        kind: "unknown",
                        error: `保存请求结果不确定：${String(error?.message || error)}`
                    };
                }
            })()
            """;

        return await ExecuteAsync<SaveTicketResult>(script, cancellationToken);
    }

    public async Task<VerificationResult> VerifyCreatedAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        var caseIdJson = JsonSerializer.Serialize(caseId);
        var script =
            $$"""
            (async () => {
                const caseId = {{caseIdJson}};
                try {
                    const response = await fetch("/xzsw/zcaseManager/getTrackInfo.do", {
                        method: "POST",
                        cache: "no-store",
                        credentials: "same-origin",
                        headers: { "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8" },
                        body: new URLSearchParams({ case_id: caseId, flag: "11" })
                    });
                    const html = await response.text();
                    if (response.url?.includes("/login") || html.includes('"sessionstatus":"timeout"')) {
                        return { ok: false, kind: "session", error: "技术支持系统登录已失效" };
                    }
                    if (!response.ok) {
                        return { ok: false, kind: "protocol", error: `核验请求失败：HTTP ${response.status}` };
                    }
                    const text = new DOMParser()
                        .parseFromString(html, "text/html")
                        .body?.innerText
                        ?.replace(/\s+/g, " ")
                        ?.trim() || "";
                    const created = text.includes("创建");
                    return {
                        ok: true,
                        data: {
                            isCreated: created,
                            message: created
                                ? `已按编号 ${caseId} 核验创建记录`
                                : `未按编号 ${caseId} 查询到创建记录`
                        }
                    };
                } catch (error) {
                    return {
                        ok: false,
                        kind: "network",
                        error: `核验请求失败：${String(error?.message || error)}`
                    };
                }
            })()
            """;

        return await ExecuteAsync<VerificationResult>(script, cancellationToken);
    }

    public async Task<AcceptanceResult> AcceptAndHandleAsync(
        string caseId,
        string processingRemark,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(caseId) || !caseId.All(char.IsDigit))
        {
            throw new InvalidDataException("技术支持编号无效，无法执行受理并处理。");
        }

        if (string.IsNullOrWhiteSpace(processingRemark))
        {
            throw new InvalidDataException("处理说明不能为空，无法执行受理并处理。");
        }

        var inputJson = JsonSerializer.Serialize(new
        {
            caseId,
            processingRemark = processingRemark.Trim(),
            formPath = SupportPlatformRoutes.BuildAcceptAndHandleFormPath(caseId)
        });
        var script =
            $$"""
            (async () => {
                const input = {{inputJson}};
                let saveIssued = false;
                let acceptanceFrame = null;
                const sessionTimeout = (text, url) =>
                    url?.includes("/login") ||
                    String(text || "").includes('"sessionstatus":"timeout"');
                // 平台的受理页不是事件申请保存后的自动跳转页。使用同会话 iframe
                // 实际打开受理页，避免以 XHR/fetch 方式读取时被平台返回当前工作台页面。
                const loadAcceptancePage = (formPath) => new Promise(resolve => {
                    const frame = document.createElement("iframe");
                    frame.setAttribute("aria-hidden", "true");
                    frame.tabIndex = -1;
                    frame.style.cssText = "position:fixed;width:1px;height:1px;left:-10000px;top:-10000px;border:0;visibility:hidden";
                    let settled = false;
                    const finish = value => {
                        if (settled) return;
                        settled = true;
                        clearTimeout(timeout);
                        resolve({ ...value, frame });
                    };
                    const timeout = setTimeout(() => finish({
                        error: "打开受理并处理页面超时"
                    }), 20000);
                    frame.addEventListener("load", () => {
                        try {
                            const loadedDocument = frame.contentDocument;
                            const loadedWindow = frame.contentWindow;
                            if (!loadedDocument || !loadedWindow) {
                                finish({ error: "受理并处理页面加载后无法读取页面内容" });
                                return;
                            }
                            finish({
                                documentValue: loadedDocument,
                                pageWindow: loadedWindow,
                                pageUrl: String(loadedWindow.location.href || ""),
                                pageTitle: String(loadedDocument.title || ""),
                                pageText: String(loadedDocument.body?.innerText || "")
                            });
                        } catch (error) {
                            finish({ error: `受理并处理页面无法读取：${String(error?.message || error)}` });
                        }
                    }, { once: true });
                    frame.src = formPath;
                    (document.body || document.documentElement).appendChild(frame);
                });
                const appendControl = (body, control) => {
                    const name = String(control.name || "").trim();
                    if (!name || control.disabled) return;
                    const type = String(control.type || "").toLowerCase();
                    if ((type === "checkbox" || type === "radio") && !control.checked) return;
                    body.append(name, String(control.value ?? ""));
                };

                try {
                    const loaded = await loadAcceptancePage(input.formPath);
                    acceptanceFrame = loaded.frame;
                    const cleanupFrame = () => {
                        acceptanceFrame?.remove();
                        acceptanceFrame = null;
                    };
                    if (loaded.error) {
                        return {
                            ok: false,
                            kind: "protocol",
                            error: loaded.error
                        };
                    }
                    if (sessionTimeout(loaded.pageText, loaded.pageUrl)) {
                        cleanupFrame();
                        return { ok: false, kind: "session", error: "技术支持系统登录已失效" };
                    }

                    const documentValue = loaded.documentValue;
                    const form = documentValue.querySelector("form");
                    if (!form || !documentValue.body?.innerText?.includes("事件受理并处理")) {
                        cleanupFrame();
                        return {
                            ok: false,
                            kind: "protocol",
                            error: `平台返回的页面不是受理并处理表单（地址：${loaded.pageUrl || "未知"}；标题：${loaded.pageTitle || "无"}），请检查登录身份或平台页面是否变化`
                        };
                    }

                    const required = [
                        "case_id",
                        "flag",
                        "handle_id",
                        "action_type",
                        "case_creator_id",
                        "proposer_id",
                        "processingType",
                        "type_id",
                        "system",
                        "remark"
                    ];
                    const missing = required.filter(name =>
                        !form.querySelector(`[name="${name}"]`));
                    if (missing.length > 0) {
                        cleanupFrame();
                        return {
                            ok: false,
                            kind: "protocol",
                            error: `受理并处理表单字段发生变化：${missing.join("、")}`
                        };
                    }

                    const returnedCaseId = String(
                        form.querySelector('[name="case_id"]')?.value || "").trim();
                    if (returnedCaseId !== input.caseId) {
                        cleanupFrame();
                        return {
                            ok: false,
                            kind: "protocol",
                            error: "受理并处理表单返回的技术支持编号不匹配，已停止操作"
                        };
                    }

                    const body = new URLSearchParams();
                    for (const control of form.querySelectorAll("input[name], select[name], textarea[name]")) {
                        appendControl(body, control);
                    }
                    body.set("remark", input.processingRemark);

                    saveIssued = true;
                    const saveResponse = await loaded.pageWindow.fetch("/xzsw/zcaseManager/saveAcceptAndHandleCase.do", {
                        method: "POST",
                        cache: "no-store",
                        credentials: "same-origin",
                        headers: {
                            "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8",
                            "X-Requested-With": "XMLHttpRequest"
                        },
                        body
                    });
                    const saveText = await saveResponse.text();
                    cleanupFrame();
                    if (sessionTimeout(saveText, saveResponse.url)) {
                        return {
                            ok: false,
                            kind: "unknown",
                            error: "受理并处理请求发出后登录失效，结果需要人工核验"
                        };
                    }
                    if (!saveResponse.ok) {
                        return {
                            ok: false,
                            kind: "unknown",
                            error: `受理并处理保存返回 HTTP ${saveResponse.status}，结果需要人工核验`
                        };
                    }

                    let payload = null;
                    try {
                        payload = JSON.parse(saveText);
                    } catch {
                    }
                    const explicitFailure =
                        payload?.success === false ||
                        payload?.result === false ||
                        payload?.status === false ||
                        /保存失败|操作失败|处理失败/.test(saveText);
                    if (explicitFailure) {
                        return {
                            ok: true,
                            data: {
                                isAccepted: false,
                                message: "平台明确返回受理并处理失败"
                            }
                        };
                    }

                    return {
                        ok: true,
                        data: {
                            isAccepted: true,
                            message: "受理并处理保存请求已返回"
                        }
                    };
                } catch (error) {
                    return {
                        ok: false,
                        kind: saveIssued ? "unknown" : "protocol",
                        error: saveIssued
                            ? `受理并处理保存后结果不明确：${String(error?.message || error)}`
                            : `受理并处理前置请求失败：${String(error?.message || error)}`
                    };
                } finally {
                    acceptanceFrame?.remove();
                }
            })()
            """;

        var saved = await ExecuteAsync<AcceptanceResult>(script, cancellationToken);
        if (!saved.IsAccepted)
        {
            return saved;
        }

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var verification = await VerifyAcceptedAndHandledAsync(caseId, cancellationToken);
            if (verification.IsAccepted)
            {
                return verification;
            }

            if (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        throw new SubmissionOutcomeUnknownException(
            "受理并处理保存请求已发出，但办件仍处于已创建状态，结果需要人工核验。");
    }

    public async Task<AcceptanceResult> VerifyAcceptedAndHandledAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(caseId) || !caseId.All(char.IsDigit))
        {
            throw new InvalidDataException("技术支持编号无效，无法核验受理并处理结果。");
        }

        var query = await QueryPendingAsync(
            new PendingTicketQueryCriteria(Title: string.Empty, CaseId: caseId),
            cancellationToken);
        var item = query.Items.FirstOrDefault(ticket =>
            string.Equals(ticket.CaseId, caseId, StringComparison.Ordinal));
        if (item is null && query.Total == 0)
        {
            return new AcceptanceResult(true, "办件已离开未受理列表");
        }

        var status = item?.StatusName?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(status) &&
            !string.Equals(status, "已创建", StringComparison.Ordinal))
        {
            return new AcceptanceResult(true, $"办件状态已更新为“{status}”");
        }

        return new AcceptanceResult(false, "办件仍处于已创建状态");
    }

    public async Task<PendingTicketQueryResult> QueryPendingAsync(
        string title,
        CancellationToken cancellationToken = default) =>
        await QueryPendingAsync(PendingTicketQueryCriteria.FromTitle(title), cancellationToken);

    public async Task<PendingTicketQueryResult> QueryPendingAsync(
        PendingTicketQueryCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        var criteriaJson = JsonSerializer.Serialize(criteria);
        var script =
            $$"""
            (async () => {
                const criteria = {{criteriaJson}};
                const value = name => criteria[name] || criteria[name[0].toLowerCase() + name.slice(1)] || "";
                const pageSize = 100;
                const all = [];

                const loadPage = async pageIndex => {
                    const body = new URLSearchParams({
                        pageindex: "",
                        pagesize: "",
                        contentvalue: "",
                        sele: "",
                        flag: "1",
                        systemflag: "0",
                        case_id: value("CaseId"),
                        case_title: value("Title"),
                        type_id: value("TypeId"),
                        assigned_to_name: value("AssigneeName"),
                        assigned_to: "",
                        system: value("SystemId"),
                        status_id: value("StatusId"),
                        case_creator_name: value("ApplicantName"),
                        case_creator_id: "",
                        case_creator_org: value("CreatorOrg"),
                        case_creator_orgid: "",
                        handler_name: value("CurrentHandlerName"),
                        handler: "",
                        startdate: value("StartDate"),
                        enddate: value("EndDate"),
                        closestartdate: value("CloseStartDate"),
                        closeenddate: value("CloseEndDate"),
                        px: value("SortField"),
                        haveJr: value("HaveJr"),
                        jr_id: value("JiraId"),
                        jr_status: value("JiraStatus"),
                        case_description: value("Description"),
                        qsession: "0",
                        pageIndex: String(pageIndex),
                        pageSize: String(pageSize),
                        sortField: value("SortField"),
                        sortOrder: ""
                    });
                    const response = await fetch("/xzsw/zcaseQuery/getPage.do", {
                        method: "POST",
                        cache: "no-store",
                        credentials: "same-origin",
                        headers: {
                            "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8",
                            "X-Requested-With": "XMLHttpRequest"
                        },
                        body
                    });
                    const text = await response.text();
                    if (response.url?.includes("/login") ||
                        text.includes('"sessionstatus":"timeout"')) {
                        return { sessionExpired: true };
                    }
                    if (!response.ok) {
                        throw new Error(`待受理查询返回 HTTP ${response.status}`);
                    }
                    return JSON.parse(text);
                };

                try {
                    const first = await loadPage(0);
                    if (first.sessionExpired) {
                        return { ok: false, kind: "session", error: "技术支持系统登录已失效" };
                    }
                    const total = Number(first.total || 0);
                    all.push(...(Array.isArray(first.data) ? first.data : []));
                    const pages = Math.ceil(total / pageSize);
                    for (let page = 1; page < pages; page++) {
                        const next = await loadPage(page);
                        if (next.sessionExpired) {
                            return { ok: false, kind: "session", error: "技术支持系统登录已失效" };
                        }
                        all.push(...(Array.isArray(next.data) ? next.data : []));
                    }

                    const cleanTime = value => String(value || "").replace(/\.0$/, "");
                    return {
                        ok: true,
                        data: {
                            total,
                            items: all.map(item => ({
                                caseId: String(item.caseId ?? ""),
                                caseTitle: String(item.caseTitle ?? ""),
                                typeName: String(item.typeName ?? ""),
                                systemName: String(item.systemName ?? ""),
                                assignee: String(item.slr ?? ""),
                                currentHandler: String(item.dqclr ?? ""),
                                applicant: String(item.sqr ?? ""),
                                createTime: cleanTime(item.createTimeStr),
                                latestReplyTime: cleanTime(item.operateTimeStr),
                                statusName: String(item.statusName ?? "")
                            }))
                        }
                    };
                } catch (error) {
                    return {
                        ok: false,
                        kind: "protocol",
                        error: `待受理查询响应异常：${String(error?.message || error)}`
                    };
                }
            })()
            """;

        return await ExecuteAsync<PendingTicketQueryResult>(script, cancellationToken);
    }

    public async Task<CloseTicketResult> CloseTicketAsync(
        PendingTicketRow ticket,
        CloseTicketSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket.CaseId) ||
            !ticket.CaseId.All(char.IsDigit))
        {
            throw new InvalidDataException("技术支持编号无效，无法执行关闭。");
        }

        var inputJson = JsonSerializer.Serialize(new
        {
            caseId = ticket.CaseId,
            title = ticket.CaseTitle,
            causeTypeValue = settings.CauseTypeValue,
            causeTypeName = settings.CauseTypeName,
            causeTypePath = settings.CauseTypePath,
            causeDescription = settings.CauseDescription,
            solveTypeValue = settings.SolveTypeValue,
            solveTypeName = settings.SolveTypeName,
            solutionDescription = settings.SolutionDescription,
            solutionFormPath = SupportPlatformRoutes.BuildSolutionFormPath(ticket.CaseId)
        });
        var script =
            $$"""
            (async () => {
                const input = {{inputJson}};
                let saveIssued = false;

                const sessionTimeout = (text, url) =>
                    url?.includes("/login") ||
                    String(text || "").includes('"sessionstatus":"timeout"');

                const readSolutionForm = async () => {
                    const response = await fetch(input.solutionFormPath, {
                        method: "GET",
                        cache: "no-store",
                        credentials: "same-origin"
                    });
                    const html = await response.text();
                    if (sessionTimeout(html, response.url)) {
                        return { sessionExpired: true };
                    }
                    if (!response.ok) {
                        return {
                            error: `读取受理总结表单失败：HTTP ${response.status}`
                        };
                    }
                    if (!html.includes("cause_description") ||
                        !html.includes("solve_type") ||
                        !html.includes("solution_description")) {
                        return {
                            error: "平台返回的页面不是受理总结表单，请检查登录状态或平台页面是否变化"
                        };
                    }
                    return { html };
                };

                const appendControl = (body, control) => {
                    const name = String(control.name || "").trim();
                    if (!name || control.disabled) return;
                    const type = String(control.type || "").toLowerCase();
                    if ((type === "checkbox" || type === "radio") && !control.checked) return;
                    if (control.tagName === "SELECT" && control.multiple) {
                        for (const option of control.selectedOptions) {
                            body.append(name, option.value);
                        }
                        return;
                    }
                    body.append(name, String(control.value ?? ""));
                };

                const queryPendingCase = async () => {
                    const response = await fetch("/xzsw/zcaseQuery/getPage.do", {
                        method: "POST",
                        cache: "no-store",
                        credentials: "same-origin",
                        headers: {
                            "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8",
                            "X-Requested-With": "XMLHttpRequest"
                        },
                        body: new URLSearchParams({
                            pageindex: "",
                            pagesize: "",
                            contentvalue: "",
                            sele: "",
                            flag: "1",
                            systemflag: "0",
                            case_id: input.caseId,
                            case_title: "",
                            type_id: "",
                            assigned_to_name: "",
                            assigned_to: "",
                            system: "",
                            status_id: "",
                            case_creator_name: "",
                            case_creator_id: "",
                            case_creator_org: "",
                            case_creator_orgid: "",
                            handler_name: "",
                            handler: "",
                            startdate: "",
                            enddate: "",
                            closestartdate: "",
                            closeenddate: "",
                            px: "",
                            haveJr: "",
                            jr_id: "",
                            jr_status: "",
                            case_description: "",
                            qsession: "0",
                            pageIndex: "0",
                            pageSize: "20",
                            sortField: "",
                            sortOrder: ""
                        })
                    });
                    const text = await response.text();
                    if (sessionTimeout(text, response.url)) {
                        return { sessionExpired: true };
                    }
                    if (!response.ok) {
                        throw new Error(`关闭结果核验返回 HTTP ${response.status}`);
                    }
                    const payload = JSON.parse(text);
                    const items = Array.isArray(payload.data) ? payload.data : [];
                    const item = items.find(row => String(row.caseId ?? "") === input.caseId);
                    return {
                        found: Boolean(item),
                        total: Number(payload.total || 0),
                        status: String(item?.statusName ?? "")
                    };
                };

                try {
                    const loaded = await readSolutionForm();
                    if (loaded.sessionExpired) {
                        return { ok: false, kind: "session", error: "技术支持系统登录已失效" };
                    }
                    if (!loaded.html) {
                        return {
                            ok: false,
                            kind: "protocol",
                            error: loaded.error || "未读取到受理总结表单"
                        };
                    }

                    const documentValue = new DOMParser().parseFromString(loaded.html, "text/html");
                    const form = documentValue.querySelector("form");
                    if (!form) {
                        return {
                            ok: false,
                            kind: "protocol",
                            error: "受理总结页面未找到提交表单"
                        };
                    }

                    const findControl = name =>
                        form.querySelector(`[name="${name}"]`) ||
                        documentValue.getElementById(name);
                    const readSetting = (name, fallback) => {
                        const value = String(input?.[name] ?? "").trim();
                        return value || fallback;
                    };
                    const resolveSelectValue = (control, configuredValue, configuredName, displayName) => {
                        const value = String(configuredValue || "").trim();
                        if (value) {
                            return { value };
                        }

                        const targetName = String(configuredName || "").trim();
                        if (!targetName || !control || control.tagName !== "SELECT") {
                            return {
                                error: `${displayName}未配置平台值，无法自动提交`
                            };
                        }

                        const matched = Array.from(control.options || []).find(option => {
                            const text = String(option.textContent || "").trim();
                            return text === targetName ||
                                text.endsWith(targetName) ||
                                targetName.endsWith(text);
                        });
                        if (!matched) {
                            return {
                                error: `平台表单中未找到${displayName}“${targetName}”`
                            };
                        }

                        return { value: String(matched.value || "").trim() };
                    };
                    const required = [
                        "case_description",
                        "cause_type",
                        "cause_description",
                        "solve_type",
                        "solution_description"
                    ];
                    const missing = required.filter(name => !findControl(name));
                    if (missing.length > 0) {
                        return {
                            ok: false,
                            kind: "protocol",
                            error: `受理总结表单字段发生变化：${missing.join("、")}`
                        };
                    }

                    const eventDescription = String(findControl("case_description").value || "");
                    const causeTypeControl = findControl("cause_type");
                    const solveTypeControl = findControl("solve_type");
                    const causeType = resolveSelectValue(
                        causeTypeControl,
                        input.causeTypeValue,
                        input.causeTypeName,
                        "原因分类");
                    if (causeType.error) {
                        return {
                            ok: false,
                            kind: "protocol",
                            error: causeType.error
                        };
                    }

                    const solveType = resolveSelectValue(
                        solveTypeControl,
                        input.solveTypeValue,
                        input.solveTypeName,
                        "解决方法");
                    if (solveType.error) {
                        return {
                            ok: false,
                            kind: "protocol",
                            error: solveType.error
                        };
                    }

                    if (!eventDescription.trim()) {
                        return {
                            ok: false,
                            kind: "protocol",
                            error: "受理总结表单未带出事件描述，已停止操作"
                        };
                    }

                    const body = new URLSearchParams();
                    for (const control of form.querySelectorAll("input[name], select[name], textarea[name]")) {
                        appendControl(body, control);
                    }
                    body.set("case_id", input.caseId);
                    body.set("case_description", eventDescription);
                    body.set("cause_type", causeType.value);
                    body.set("cause_description", readSetting("causeDescription", "已处理"));
                    body.set("solve_type", solveType.value);
                    body.set("solution_description", readSetting("solutionDescription", "已处理"));

                    saveIssued = true;
                    const saveResponse = await fetch("/xzsw/zcaseManager/saveSolutionCase.do", {
                        method: "POST",
                        cache: "no-store",
                        credentials: "same-origin",
                        headers: {
                            "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8",
                            "X-Requested-With": "XMLHttpRequest"
                        },
                        body
                    });
                    const saveText = await saveResponse.text();
                    if (sessionTimeout(saveText, saveResponse.url)) {
                        return {
                            ok: false,
                            kind: "unknown",
                            error: "保存请求发出后登录失效，结果需要人工核验"
                        };
                    }
                    if (!saveResponse.ok) {
                        return {
                            ok: false,
                            kind: "unknown",
                            error: `保存请求返回 HTTP ${saveResponse.status}，结果需要人工核验`
                        };
                    }

                    let savePayload = null;
                    try {
                        savePayload = JSON.parse(saveText);
                    } catch {
                    }
                    const explicitFailure =
                        savePayload?.success === false ||
                        savePayload?.result === false ||
                        savePayload?.status === false ||
                        /保存失败|操作失败|处理失败/.test(saveText);
                    if (explicitFailure) {
                        return {
                            ok: true,
                            data: {
                                isClosed: false,
                                message: "平台明确返回受理总结失败"
                            }
                        };
                    }

                    for (let attempt = 0; attempt < 6; attempt++) {
                        const verification = await queryPendingCase();
                        if (verification.sessionExpired) {
                            return {
                                ok: false,
                                kind: "unknown",
                                error: "保存后核验时登录失效，结果需要人工核验"
                            };
                        }
                        if (verification.status.includes("已解决") ||
                            verification.status.includes("已关闭")) {
                            return {
                                ok: true,
                                data: {
                                    isClosed: true,
                                    message: verification.status || "已离开未受理列表"
                                }
                            };
                        }
                        if (!verification.found && verification.total === 0) {
                            return {
                                ok: true,
                                data: {
                                    isClosed: true,
                                    message: "已离开未受理列表"
                                }
                            };
                        }
                        await new Promise(resolve => setTimeout(resolve, 500));
                    }

                    return {
                        ok: false,
                        kind: "unknown",
                        error: "保存请求已发出，但工单仍在未受理列表，结果需要人工核验"
                    };
                } catch (error) {
                    return {
                        ok: false,
                        kind: saveIssued ? "unknown" : "protocol",
                        error: saveIssued
                            ? `保存后结果不明确：${String(error?.message || error)}`
                            : `关闭前置请求失败：${String(error?.message || error)}`
                    };
                }
            })()
            """;

        return await ExecuteAsync<CloseTicketResult>(script, cancellationToken);
    }

    private async Task<PlatformOption> ResolvePersonAsync(
        string requestedName,
        string menuId,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{menuId}\u001f{requestedName.Trim()}";
        if (_personCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var inputJson = JsonSerializer.Serialize(new { requestedName, menuId });
        var script =
            $$"""
            (async () => {
                const input = {{inputJson}};
                try {
                    const response = await fetch("/xzsw/zcaseManager/toQueryName.do", {
                        method: "POST",
                        cache: "no-store",
                        credentials: "same-origin",
                        headers: { "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8" },
                        body: new URLSearchParams({
                            qname: input.requestedName,
                            menuid: input.menuId
                        })
                    });
                    const text = await response.text();
                    if (response.url?.includes("/login") || text.includes('"sessionstatus":"timeout"')) {
                        return { ok: false, kind: "session", error: "技术支持系统登录已失效" };
                    }
                    const result = JSON.parse(text);
                    const list = Array.isArray(result.nameList) ? result.nameList : [];
                    const normalized = String(input.requestedName || "").trim();
                    let matches = [];
                    if (normalized.includes("-")) {
                        matches = list.filter(item => String(item.uname || "").trim() === normalized);
                    } else if (list.length === 1) {
                        matches = list;
                    }
                    if (matches.length !== 1) {
                        return {
                            ok: false,
                            kind: "validation",
                            error: list.length === 0
                                ? `人员“${normalized}”未匹配到平台用户`
                                : `人员“${normalized}”匹配到 ${list.length} 条，请在 Excel 填写完整“机构-姓名”`
                        };
                    }
                    return {
                        ok: true,
                        data: {
                            id: String(matches[0].uid),
                            text: String(matches[0].uname)
                        }
                    };
                } catch (error) {
                    return {
                        ok: false,
                        kind: "protocol",
                        error: `人员查询响应异常：${String(error?.message || error)}`
                    };
                }
            })()
            """;

        var resolved = await ExecuteAsync<PlatformOption>(script, cancellationToken);
        _personCache[cacheKey] = resolved;
        return resolved;
    }

    private async Task<PlatformOption> ResolveDictionaryAsync(
        string requestedText,
        string endpoint,
        IDictionary<string, PlatformOption> cache,
        string fieldName,
        CancellationToken cancellationToken)
    {
        var key = requestedText.Trim();
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var inputJson = JsonSerializer.Serialize(new { requestedText, endpoint, fieldName });
        var script =
            $$"""
            (async () => {
                const input = {{inputJson}};
                try {
                    const response = await fetch(input.endpoint, {
                        method: "POST",
                        cache: "no-store",
                        credentials: "same-origin",
                        headers: { "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8" }
                    });
                    const text = await response.text();
                    if (response.url?.includes("/login") || text.includes('"sessionstatus":"timeout"')) {
                        return { ok: false, kind: "session", error: "技术支持系统登录已失效" };
                    }
                    const source = JSON.parse(text);
                    const all = [];
                    const visit = nodes => {
                        for (const node of Array.isArray(nodes) ? nodes : []) {
                            all.push(node);
                            visit(node.children);
                        }
                    };
                    visit(source);
                    const parentIds = new Set(
                        all
                            .map(node => node.pid)
                            .filter(value => value !== null && value !== undefined)
                            .map(String)
                    );
                    const matches = all.filter(node =>
                        String(node.text || "").trim() === String(input.requestedText || "").trim() &&
                        !parentIds.has(String(node.id)) &&
                        (!Array.isArray(node.children) || node.children.length === 0)
                    );
                    if (matches.length !== 1) {
                        return {
                            ok: false,
                            kind: "validation",
                            error: `${input.fieldName}“${input.requestedText}”未唯一匹配到叶子节点`
                        };
                    }
                    return {
                        ok: true,
                        data: {
                            id: String(matches[0].id),
                            text: String(matches[0].text)
                        }
                    };
                } catch (error) {
                    return {
                        ok: false,
                        kind: "protocol",
                        error: `${input.fieldName}响应异常：${String(error?.message || error)}`
                    };
                }
            })()
            """;

        var resolved = await ExecuteAsync<PlatformOption>(script, cancellationToken);
        cache[key] = resolved;
        return resolved;
    }

    private async Task<T> ExecuteAsync<T>(string script, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_webView.CoreWebView2 is null)
        {
            throw new PlatformProtocolException("WebView2 尚未初始化。");
        }

        var requestId = $"tsbs_{Guid.NewGuid():N}";
        var requestIdJson = JsonSerializer.Serialize(requestId);
        var wrapper =
            $$"""
            (() => {
                window.__techSupportBatchResults ??= {};
                const requestId = {{requestIdJson}};
                Promise.resolve({{script}})
                    .then(value => {
                        window.__techSupportBatchResults[requestId] = {
                            completed: true,
                            value
                        };
                    })
                    .catch(error => {
                        window.__techSupportBatchResults[requestId] = {
                            completed: true,
                            value: {
                                ok: false,
                                kind: "protocol",
                                error: String(error?.message || error)
                            }
                        };
                    });
                return true;
            })()
            """;

        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(wrapper);
        }
        catch (Exception ex)
        {
            throw new PlatformProtocolException("无法在技术支持系统页面执行请求。", ex);
        }

        string? json = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var pollScript =
                    $$"""
                    (() => {
                        const requestId = {{requestIdJson}};
                        const result = window.__techSupportBatchResults?.[requestId];
                        if (!result?.completed) return null;
                        delete window.__techSupportBatchResults[requestId];
                        return result.value;
                    })()
                    """;
                var candidate = await _webView.CoreWebView2.ExecuteScriptAsync(pollScript);
                if (!string.Equals(candidate, "null", StringComparison.Ordinal))
                {
                    json = candidate;
                    break;
                }

                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlatformProtocolException("等待平台响应超时，请检查网络和登录状态。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (json is null)
        {
            throw new PlatformProtocolException("平台脚本未在限定时间内返回数据。");
        }

        ScriptEnvelope<T>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ScriptEnvelope<T>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PlatformProtocolException("平台脚本返回了无法识别的数据。", ex);
        }

        if (envelope is null)
        {
            throw new PlatformProtocolException("平台脚本未返回数据。");
        }

        if (envelope.Ok && envelope.Data is not null)
        {
            return envelope.Data;
        }

        var message = string.IsNullOrWhiteSpace(envelope.Error) ? "平台操作失败。" : envelope.Error;
        switch (envelope.Kind)
        {
            case "session":
                SessionExpired?.Invoke(this, EventArgs.Empty);
                throw new PlatformSessionExpiredException(message);
            case "unknown":
                throw new SubmissionOutcomeUnknownException(message);
            case "validation":
                throw new InvalidDataException(message);
            default:
                throw new PlatformProtocolException(message);
        }
    }

    private sealed record ScriptEnvelope<T>(bool Ok, T? Data, string? Kind, string? Error);
}
