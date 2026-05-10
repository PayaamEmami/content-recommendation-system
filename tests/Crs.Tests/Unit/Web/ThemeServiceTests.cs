using Crs.Web.Services;

namespace Crs.Tests.Unit.Web;

[TestClass]
public sealed class ThemeServiceTests
{
    [TestMethod]
    public async Task InitializeAsync_AppliesDefaultDarkTheme()
    {
        var jsRuntime = new TestJsRuntime();
        var service = new ThemeService(jsRuntime);

        await service.InitializeAsync();

        Assert.HasCount(1, jsRuntime.Calls);
        Assert.AreEqual("theme.apply", jsRuntime.Calls[0].Identifier);
        Assert.IsTrue((bool)jsRuntime.Calls[0].Args![0]!);
    }

    [TestMethod]
    public async Task ToggleThemeAsync_FlipsModeAndApplies()
    {
        var jsRuntime = new TestJsRuntime();
        var service = new ThemeService(jsRuntime);

        await service.ToggleThemeAsync();

        Assert.IsFalse(service.IsDarkMode);
        Assert.HasCount(1, jsRuntime.Calls);
        Assert.AreEqual("theme.apply", jsRuntime.Calls[0].Identifier);
        Assert.IsFalse((bool)jsRuntime.Calls[0].Args![0]!);
    }
}
