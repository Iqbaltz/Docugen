namespace Docugen.Infrastructure;

public static class PlaywrightBrowserInstaller
{
    /// <summary>Downloads the Chromium build that matches the Playwright package, plus its OS dependencies.</summary>
    public static int Install(bool withDependencies) =>
        Microsoft.Playwright.Program.Main(withDependencies ? ["install", "--with-deps", "chromium"] : ["install", "chromium"]);
}
