using Microsoft.Playwright;

namespace Flowbit.BrowserTests.Support;

/// <summary>
/// Scrolls the app's document with real wheel input. Bootstrap's reboot gives
/// the page <c>scroll-behavior: smooth</c>, so focus changes, section links,
/// Blazor's navigation reset and Playwright's own actionability scrolling all
/// animate wherever Chromium has scroll animation on. It follows the host unless
/// told otherwise, and with Windows animations off every scroll lands at once, so
/// the acceptance stack forces it on.
/// </summary>
public static class PageScrolling
{
    /// <summary>
    /// Hovering the heading could start such a scroll (it can sit under the
    /// sticky top bar), and that animation outlived a single wheel input and
    /// left the page at the heading. Point at the top bar's padding, which
    /// starts no scroll, and wheel until the page rests at the top.
    /// </summary>
    public static async Task ScrollToTopAsync(IPage page)
    {
        var bar = await page.Locator("header.topbar").BoundingBoxAsync()
            ?? throw new InvalidOperationException("The app top bar is not rendered.");
        await page.Mouse.MoveAsync(bar.X + bar.Width * 0.6f, bar.Y + 4);
        for (var wheel = 0; ; wheel++)
        {
            var y = await SettledScrollYAsync(page);
            if (y <= 1) return;
            if (wheel == 5) throw new TimeoutException($"The page stayed {y}px from the top after five wheel inputs.");
            await page.Mouse.WheelAsync(0, -(float)(y + page.ViewportSize!.Height));
        }
    }

    /// <summary>
    /// Resolves once the page's scroll position is unchanged for ten animation
    /// frames, so a smooth scroll in progress finishes before it is read.
    /// </summary>
    public static Task<double> SettledScrollYAsync(IPage page) => page.EvaluateAsync<double>("""
        () => new Promise(resolve => {
          const deadline = performance.now() + 5000;
          let last = NaN, same = 0;
          const tick = () => {
            same = scrollY === last ? same + 1 : 0;
            last = scrollY;
            if (same >= 10 || performance.now() > deadline) resolve(scrollY);
            else requestAnimationFrame(tick);
          };
          requestAnimationFrame(tick);
        })
        """);
}
