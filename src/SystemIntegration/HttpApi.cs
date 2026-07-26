using System.Net;
using System.Text;

namespace XiControl.SystemIntegration;

/// <summary>
/// Хост HTTP API (XIC-13): HttpListener поверх http.sys — встроен в .NET, без второго
/// рантайма (ASP.NET Core отвергнут осознанно, см. issue). Создаётся только при включённой
/// фиче: выключено = объекта нет = 0 CPU; включённый слушатель висит в GetContextAsync без
/// опроса. Плейнтекст-HTTP осознанно: радиус поражения белого списка мал, HTTPS не окупается.
/// </summary>
public sealed class HttpApi : IDisposable
{
    private const int MaxBodyBytes = 4096; // команды — крошечный JSON; всё крупнее не читаем

    private readonly HttpListener _listener = new();
    private readonly ApiRouter _router;

    /// <summary>Ctor запускает слушатель сразу; занятый порт и т.п. — исключение наружу,
    /// владелец логирует и живёт без API (мягкая деградация).</summary>
    public HttpApi(ApiSettings s, ApiRouter router)
    {
        _router = router;
        // '+' (все интерфейсы) требует admin-права или urlacl — у нас requireAdministrator;
        // localhost-режим держит API невидимым из сети независимо от firewall
        _listener.Prefixes.Add(s.LanAccess ? $"http://+:{s.Port}/" : $"http://127.0.0.1:{s.Port}/");
        _listener.Start();
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                if (!_listener.IsListening) return; // Dispose прервал ожидание — штатный выход
                Log.Ex("HttpApi.Accept", ex);
                continue;
            }
            try { Serve(ctx); }
            catch (Exception ex)
            {
                Log.Ex("HttpApi.Serve", ex);
                try { ctx.Response.Abort(); } catch { /* соединение уже мертво */ }
            }
        }
    }

    private void Serve(HttpListenerContext ctx)
    {
        int code;
        string json;
        if (!_router.CheckToken(ctx.Request.Headers["Authorization"]))
        {
            (code, json) = (401, """{"error":"unauthorized"}""");
            ctx.Response.Headers["WWW-Authenticate"] = "Bearer";
        }
        else if (ctx.Request.ContentLength64 > MaxBodyBytes)
        {
            (code, json) = (413, """{"error":"body too large"}""");
        }
        else
        {
            string body = "";
            if (ctx.Request.HasEntityBody)
                using (var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                    body = r.ReadToEnd();
            (code, json) = _router.Handle(ctx.Request.HttpMethod, ctx.Request.Url?.AbsolutePath ?? "/", body);
        }

        byte[] buf = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = buf.Length;
        ctx.Response.OutputStream.Write(buf);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); }
        catch (Exception ex) { Log.Ex("HttpApi.Dispose", ex); }
    }
}

/// <summary>
/// Firewall-правило для LAN-режима API: входящий TCP на порт, скоуп LocalSubnet — API виден
/// только своей подсети. Трогается ТОЛЬКО по явному тумблеру пользователя (как schtasks у
/// автозапуска), не на каждый старт. netsh — не с UI-потока (WaitForExit).
/// </summary>
public static class ApiFirewall
{
    private const string RuleName = "XiControl API";

    // Полный путь к netsh — не полагаемся на PATH (как SchTasks у автозапуска).
    private static readonly string Netsh =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

    /// <summary>Привести правило к желаемому состоянию (идемпотентно: сначала удаляем старое —
    /// заодно подхватывается смена порта, затем при необходимости создаём заново).</summary>
    public static void Set(bool on, int port)
    {
        Run($"advfirewall firewall delete rule name=\"{RuleName}\""); // нет правила → код 1, это не ошибка
        if (on)
            Run($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} remoteip=localsubnet");
    }

    private static void Run(string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Netsh, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            p?.WaitForExit(10_000);
        }
        catch (Exception ex) { Log.Ex("ApiFirewall", ex); }
    }
}
