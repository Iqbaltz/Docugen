using Docugen.Core;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace Docugen.Infrastructure.Rendering.Pdf;

public sealed record PdfPageSettings(string PageSize, bool Landscape, string Margin, string? FooterTemplate);

/// <summary>
/// Prints HTML to PDF with headless Chromium. One browser is shared by the whole app; every conversion gets its
/// own isolated context with JavaScript disabled and all network requests blocked.
/// </summary>
public sealed class ChromiumPdfConverter(IOptions<DocugenOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.Value.Pdf.MaxConcurrentRenders));
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task<byte[]> ConvertAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var browser = await GetBrowserAsync(cancellationToken).ConfigureAwait(false);
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = false }).ConfigureAwait(false);
            await context.RouteAsync("**/*", route => route.AbortAsync()).ConfigureAwait(false);
            await using var cancel = cancellationToken.Register(() => _ = context.CloseAsync());

            var timeout = options.Value.Pdf.RenderTimeoutSeconds * 1000f;
            var page = await context.NewPageAsync().ConfigureAwait(false);
            await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.Load, Timeout = timeout }).ConfigureAwait(false);

            var pdf = await page.PdfAsync(new PagePdfOptions
            {
                Format = settings.PageSize,
                Landscape = settings.Landscape,
                PrintBackground = true,
                PreferCSSPageSize = true,
                DisplayHeaderFooter = settings.FooterTemplate is not null,
                HeaderTemplate = "<span></span>",
                FooterTemplate = settings.FooterTemplate ?? "<span></span>",
                Margin = new Margin { Top = settings.Margin, Bottom = settings.Margin, Left = settings.Margin, Right = settings.Margin },
            }).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return pdf;
        }
        catch (PlaywrightException) when (cancellationToken.IsCancellationRequested)
        {
            // The context was closed because the caller went away; report it as a cancellation, not a failure.
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is { IsConnected: true } ready)
        {
            return ready;
        }

        await _launchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_browser is { IsConnected: true } launched)
            {
                return launched;
            }

            if (_browser is not null)
            {
                await _browser.DisposeAsync().ConfigureAwait(false);
            }

            _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
            var executablePath = options.Value.Pdf.BrowserExecutablePath;
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath,
                Args = ["--disable-dev-shm-usage", "--disable-gpu"],
            }).ConfigureAwait(false);
            return _browser;
        }
        finally
        {
            _launchLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync().ConfigureAwait(false);
        }

        _playwright?.Dispose();
        _launchLock.Dispose();
        _slots.Dispose();
    }
}
