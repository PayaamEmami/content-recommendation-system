using Microsoft.AspNetCore.Components;

namespace Crs.Tests.Unit.Web;

internal sealed class TestNavigationManager : NavigationManager
{
    public TestNavigationManager(string baseUri = "https://example.com/")
    {
        Initialize(baseUri, baseUri);
    }

    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        Uri = ToAbsoluteUri(uri).ToString();
    }
}

