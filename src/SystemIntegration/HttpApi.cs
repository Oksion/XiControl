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

    /// <summary>Потолок на весь обмен — чтение тела и запись ответа. Запросы обслуживаются по
    /// одному, поэтому клиент, присылающий тело по байту, без потолка держал бы API для всех
    /// (XIC-80). Командам хватает миллисекунд — 10 с с запасом на медленный Wi-Fi.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    // JSON — всегда UTF-8 (RFC 8259); битые байты — ошибка, а не тихие «?» в значении команды
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly HttpListener _listener = new();
    private readonly ApiRouter _router;

    /// <summary>Ctor запускает слушатель сразу; занятый порт и т.п. — исключение наружу,
    /// владелец логирует и живёт без API (мягкая деградация).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S5332",
        Justification = "Локальный API на HttpListener: без TLS по замыслу — localhost или домашняя сеть, доступ по токену (docs/15)")]
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
            using var deadline = new CancellationTokenSource(Deadline);
            try { await Serve(ctx, deadline.Token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                // по таймауту не шумим стеком: медленный клиент — не сбой приложения
                if (deadline.IsCancellationRequested) Log.Write("HttpApi: клиент не уложился в 10 с — соединение сброшено");
                else Log.Ex("HttpApi.Serve", ex);
                // Abort рвёт соединение — заодно прерывает брошенное по таймауту чтение
                try { ctx.Response.Abort(); } catch { /* соединение уже мертво */ }
            }
        }
    }

    private async Task Serve(HttpListenerContext ctx, CancellationToken ct)
    {
        int code;
        string json;
        // Токен — до чтения тела: без него тело не читаем вовсе, сколько бы его ни прислали
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
            var (body, error) = ctx.Request.HasEntityBody
                ? await ReadBody(ctx.Request.InputStream, MaxBodyBytes, ct).ConfigureAwait(false)
                : ("", BodyError.None);
            (code, json) = error switch
            {
                BodyError.TooLarge => (413, """{"error":"body too large"}"""),
                BodyError.BadUtf8 => (400, """{"error":"bad request"}"""),
                _ => _router.Handle(ctx.Request.HttpMethod, ctx.Request.Url?.AbsolutePath ?? "/", body),
            };
        }

        // Тело могло остаться недочитанным (401/413) — keep-alive на таком соединении не держим
        if (code is 401 or 413) ctx.Response.KeepAlive = false;
        byte[] buf = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = buf.Length;
        await ctx.Response.OutputStream.WriteAsync(buf, ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
        ctx.Response.Close();
    }

    internal enum BodyError { None, TooLarge, BadUtf8 }

    /// <summary>
    /// Прочитать тело не больше <paramref name="maxBytes"/> (XIC-80). Проверки по Content-Length
    /// мало: у chunked-запроса длины нет вовсе, и прежний <c>ReadToEnd</c> читал сколько пришлют.
    /// Читаем в буфер на байт больше лимита — переполнение видно без дочитывания хвоста.
    /// <paramref name="ct"/> обрывает и зависшее чтение: <c>WaitAsync</c> бросает его, сокет
    /// закрывает вызывающий (поток http.sys отмену сам не слушает).
    /// </summary>
    internal static async Task<(string Body, BodyError Error)> ReadBody(Stream input, int maxBytes, CancellationToken ct)
    {
        byte[] buf = new byte[maxBytes + 1];
        int count = 0;
        while (count < buf.Length)
        {
            int read = await input.ReadAsync(buf.AsMemory(count), ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count > maxBytes) return ("", BodyError.TooLarge);

        // BOM снимал прежний StreamReader — клиентам, что его шлют, ничего не ломаем
        int start = count >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF ? 3 : 0;
        try { return (StrictUtf8.GetString(buf, start, count - start), BodyError.None); }
        catch (DecoderFallbackException) { return ("", BodyError.BadUtf8); }
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
