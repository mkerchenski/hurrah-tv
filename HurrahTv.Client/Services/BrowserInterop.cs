using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HurrahTv.Client.Services;

// thin C# wrappers around the ES modules under wwwroot/js/. Each module is loaded
// lazily on first call and cached for the DI scope's lifetime (one import per app
// session in WASM). The IConfiguration BuildVersion suffix matches the cache-busting
// CI step so a deploy invalidates cached module files.
//
// These services replaced the inline window.hurrah* globals that used to live in
// index.html — see issue #87.

internal static class JsModulePath
{
    public static string For(string relativePath, IConfiguration config)
    {
        string? version = config["BuildVersion"];
        return string.IsNullOrEmpty(version) ? relativePath : $"{relativePath}?v={version}";
    }
}

public sealed class ScrollService(IJSRuntime js, IConfiguration config) : IAsyncDisposable
{
    private readonly string _path = JsModulePath.For("./js/scroll.js", config);
    private IJSObjectReference? _module;

    public async Task ScrollRowAsync(ElementReference container, double delta)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", _path);
        await _module.InvokeVoidAsync("scrollRow", container, delta);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); } catch { }
        _module = null;
    }
}

public sealed class InstallBannerService(IJSRuntime js, IConfiguration config) : IAsyncDisposable
{
    private readonly string _path = JsModulePath.For("./js/install.js", config);
    private IJSObjectReference? _module;

    public async Task<bool> ShouldShowAsync()
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", _path);
        return await _module.InvokeAsync<bool>("shouldShow");
    }

    public async Task DismissAsync()
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", _path);
        try { await _module.InvokeVoidAsync("dismiss"); } catch { }
    }

    // Android / desktop Chrome: whether a programmatic install prompt is available
    public async Task<bool> CanPromptInstallAsync()
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", _path);
        return await _module.InvokeAsync<bool>("canPromptInstall");
    }

    // returns true if the user accepted the native install prompt
    public async Task<bool> PromptInstallAsync()
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", _path);
        try { return await _module.InvokeAsync<bool>("promptInstall"); }
        catch (JSException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); } catch { }
        _module = null;
    }
}

public sealed class VersionService(IJSRuntime js, IConfiguration config) : IAsyncDisposable
{
    private readonly string _path = JsModulePath.For("./js/version.js", config);
    private IJSObjectReference? _module;

    public async Task<bool> CheckAsync()
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", _path);
        return await _module.InvokeAsync<bool>("check");
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); } catch { }
        _module = null;
    }
}

public sealed class LongPressService(IJSRuntime js, IConfiguration config) : IAsyncDisposable
{
    private readonly string _path = JsModulePath.For("./js/longpress.js", config);
    private IJSObjectReference? _module;

    // returns an opaque JS-side handle the caller passes back to DetachAsync. T is the
    // component class that owns the [JSInvokable] callback (PosterCard, WatchlistRow).
    public async Task<IJSObjectReference?> AttachAsync<T>(
        ElementReference el,
        DotNetObjectReference<T> dotNetRef,
        string callbackName,
        int itemId) where T : class
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", _path);
        return await _module.InvokeAsync<IJSObjectReference>("attach", el, dotNetRef, callbackName, itemId);
    }

    public async Task DetachAsync(IJSObjectReference? handle)
    {
        if (handle is null || _module is null) return;
        try { await _module.InvokeVoidAsync("cleanup", handle); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); } catch { }
        _module = null;
    }
}

public sealed class TitleService(IJSRuntime js, IConfiguration config) : IAsyncDisposable
{
    private readonly string _path = JsModulePath.For("./js/title.js", config);
    private IJSObjectReference? _module;

    public async Task ApplyEnvPrefixAsync(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return;
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", _path);
        await _module.InvokeVoidAsync("applyEnvPrefix", prefix);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); } catch { }
        _module = null;
    }
}

public sealed class SortableService(IJSRuntime js, IConfiguration config) : IAsyncDisposable
{
    private readonly string _path = JsModulePath.For("./js/sortable.js", config);
    private IJSObjectReference? _module;

    // SortableJS itself is loaded as a global via the CDN <script> in index.html.
    // This service only wraps our thin wiring around onEnd.
    public async Task<IJSObjectReference?> InitAsync<T>(
        ElementReference container,
        DotNetObjectReference<T> dotNetRef,
        string callbackName,
        object? options = null) where T : class
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", _path);
        return await _module.InvokeAsync<IJSObjectReference>("init", container, dotNetRef, callbackName, options);
    }

    public async Task DisposeHandleAsync(IJSObjectReference? handle)
    {
        if (handle is null || _module is null) return;
        try { await _module.InvokeVoidAsync("dispose", handle); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); } catch { }
        _module = null;
    }
}
