using System.Net;
using System.Net.Http;
using LibGit2Sharp;

namespace QxBgmManager.Services;

/// <summary>
/// 全局 HTTP(S) 代理配置：设置页写入 <see cref="Models.AppSettings.ProxyUrl"/> 后经
/// <see cref="Apply"/> 生效。作用范围：全部 HttpClient（市场清单/音频下载/自动更新，
/// 经 HttpClient.DefaultProxy 注入）与 git 索引同步（LibGit2Sharp ProxyOptions）。
/// WebView2 奇域浏览跟随系统代理，不受此项控制。留空恢复系统默认代理。
/// </summary>
public static class ProxyConfig
{
    /// <summary>进程启动后第一次访问时捕获的系统默认代理，清空配置后恢复用。</summary>
    private static readonly IWebProxy? SystemDefault = HttpClient.DefaultProxy;

    /// <summary>当前已生效的配置值（原始字符串，未配置为空）。</summary>
    public static string Current { get; private set; } = "";

    /// <summary>
    /// 解析并全局应用代理。返回 null 表示成功；返回错误文本表示地址无效（保持原状不改动）。
    /// 空白地址 = 清除自定义代理并恢复系统默认。
    /// </summary>
    public static string? Apply(string? url)
    {
        url = url?.Trim() ?? "";
        if (url.Length == 0)
        {
            HttpClient.DefaultProxy = SystemDefault;
            Current = "";
            return null;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "地址需为 http:// 或 https:// 开头的完整 URL（暂不支持 socks）";
        HttpClient.DefaultProxy = new WebProxy(uri);
        Current = url;
        return null;
    }

    /// <summary>git 同步用的代理选项；未配置返回 Auto（LibGit2Sharp 走系统默认）。</summary>
    public static ProxyOptions GitProxyOptions()
    {
        var opts = new ProxyOptions();
        if (Current.Length > 0)
        {
            opts.ProxyType = ProxyType.Specified;
            opts.Url = Current;
        }
        return opts;
    }

    /// <summary>把当前代理写入既有 FetchOptions（其 ProxyOptions 属性只读但实例可变）。</summary>
    public static void ApplyTo(FetchOptions fetchOptions)
    {
        var opts = GitProxyOptions();
        fetchOptions.ProxyOptions.ProxyType = opts.ProxyType;
        fetchOptions.ProxyOptions.Url = opts.Url;
    }

    /// <summary>带当前代理的短超时探测 HttpClient，设置页"测试连接"用。</summary>
    public static HttpClient CreateTestClient()
        => new(new HttpClientHandler { Proxy = HttpClient.DefaultProxy, UseProxy = true })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
}
