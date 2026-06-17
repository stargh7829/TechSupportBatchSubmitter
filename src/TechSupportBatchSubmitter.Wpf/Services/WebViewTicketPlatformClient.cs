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
            (() => {
                const url = String(location.href || "");
                const title = String(document.title || "");
                const body = String(document.body?.innerText || "");
                const supportReady =
                    url.includes("172.18.75.6:18005/xzsw") &&
                    body.includes("技术支持");
                const workbenchReady =
                    url.includes("172.18.75.21") &&
                    body.includes("你好！");
                const supportName = body.match(/公司\s*-\s*([^\s]+)/)?.[1] || "";
                const workbenchName = body.match(/你好！\s*([^\s]+)/)?.[1] || "";
                return {
                    ok: true,
                    data: {
                        isSupportPlatformReady: supportReady,
                        isAuthenticated: supportReady || workbenchReady,
                        displayName: supportName || workbenchName,
                        message: supportReady
                            ? "技术支持系统已登录"
                            : workbenchReady
                                ? "工作台已登录，请点击“技术支持”进入系统"
                                : "请在左侧登录页面完成登录"
                    }
                };
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
                    if (url?.includes("172.18.75.21") || text.includes('"sessionstatus":"timeout"')) {
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
        var payload = new
        {
            case_id = caseId,
            saveFlag = "1",
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
                    const response = await fetch("/xzsw/zcaseManager/saveCase.do", {
                        method: "POST",
                        cache: "no-store",
                        credentials: "same-origin",
                        headers: { "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8" },
                        body: new URLSearchParams(payload)
                    });
                    const text = await response.text();
                    if (response.url?.includes("172.18.75.21") || text.includes('"sessionstatus":"timeout"')) {
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
        TicketRow expected,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = JsonSerializer.Serialize(new
        {
            caseId,
            title = expected.Title,
            description = expected.Description
        });
        var script =
            $$"""
            (async () => {
                const expected = {{payloadJson}};
                try {
                    const response = await fetch("/xzsw/zcaseManager/getTrackInfo.do", {
                        method: "POST",
                        cache: "no-store",
                        credentials: "same-origin",
                        headers: { "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8" },
                        body: new URLSearchParams({ case_id: expected.caseId, flag: "11" })
                    });
                    const html = await response.text();
                    if (response.url?.includes("172.18.75.21") || html.includes('"sessionstatus":"timeout"')) {
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
                    const created =
                        text.includes("创建") &&
                        text.includes(expected.title) &&
                        text.includes(expected.description);
                    return {
                        ok: true,
                        data: {
                            isCreated: created,
                            message: created ? "已核验创建记录" : "未查询到匹配的创建记录"
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
                    if (response.url?.includes("172.18.75.21") ||
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
            solutionFormPath = SupportPlatformRoutes.BuildSolutionFormPath(ticket.CaseId)
        });
        var script =
            $$"""
            (async () => {
                const input = {{inputJson}};
                let saveIssued = false;

                const sessionTimeout = (text, url) =>
                    url?.includes("172.18.75.21") ||
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
                    body.set("cause_type", "21");
                    body.set("cause_description", "已处理");
                    body.set("solve_type", "2");
                    body.set("solution_description", "已处理");

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
                    if (response.url?.includes("172.18.75.21") || text.includes('"sessionstatus":"timeout"')) {
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
                    if (response.url?.includes("172.18.75.21") || text.includes('"sessionstatus":"timeout"')) {
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
