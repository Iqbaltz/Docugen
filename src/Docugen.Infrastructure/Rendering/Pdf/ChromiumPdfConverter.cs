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

    /// <summary>
    /// Prints <paramref name="html"/> to PDF. Returns promptly when cancelled, and fails with a
    /// <see cref="TimeoutException"/> after <c>Pdf:RenderTimeoutSeconds</c>, so a stuck Chromium can never hold a
    /// render slot for good.
    /// </summary>
    public async Task<byte[]> ConvertAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var timeoutSeconds = options.Value.Pdf.RenderTimeoutSeconds;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var token = limit.Token;
        IBrowserContext? context = null;
        try
        {
            var browser = await GetBrowserAsync(cancellationToken).ConfigureAwait(false);

            // Playwright calls take no cancellation token, so stop waiting on them instead. Closing the context while
            // a call is still in flight can leave that call pending forever, so the context is only closed afterwards.
            var creating = browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = false });
            try
            {
                context = await creating.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = creating.ContinueWith(t => CloseQuietlyAsync(t.Result), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
                throw;
            }

            await context.RouteAsync("**/*", route => route.AbortAsync()).WaitAsync(token).ConfigureAwait(false);
            var page = await context.NewPageAsync().WaitAsync(token).ConfigureAwait(false);
            await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.Load, Timeout = timeoutSeconds * 1000f }).WaitAsync(token).ConfigureAwait(false);

            return await page.PdfAsync(new PagePdfOptions
            {
                Format = settings.PageSize,
                Landscape = settings.Landscape,
                PrintBackground = true,
                PreferCSSPageSize = true,
                DisplayHeaderFooter = settings.FooterTemplate is not null,
                HeaderTemplate = "<span></span>",
                FooterTemplate = settings.FooterTemplate ?? "<span></span>",
                Margin = new Margin { Top = settings.Margin, Bottom = settings.Margin, Left = settings.Margin, Right = settings.Margin },
            }).WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && limit.IsCancellationRequested)
        {
            throw new TimeoutException($"Printing the PDF took longer than {timeoutSeconds} s (Pdf:RenderTimeoutSeconds).");
        }
        catch (PlaywrightException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            if (context is not null)
            {
                await CloseQuietlyAsync(context).ConfigureAwait(false);
            }

            _slots.Release();
        }
    }

    /// <summary>Closes a context, waiting at most a few seconds and ignoring errors: it is only being cleaned up.</summary>
    private static async Task CloseQuietlyAsync(IBrowserContext context)
    {
        try
        {
            await context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
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
