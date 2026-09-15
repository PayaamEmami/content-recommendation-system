using System.Net;
using Crs.Infrastructure.Services;

namespace Crs.Tests.Unit.Infrastructure;

[TestClass]
public sealed class UrlSafetyTests
{
    [TestMethod]
    [DataRow("http://127.0.0.1/")]
    [DataRow("http://localhost/")]
    [DataRow("http://169.254.169.254/latest/meta-data/")]
    [DataRow("http://10.0.0.5/internal")]
    [DataRow("http://192.168.1.1/")]
    [DataRow("http://172.16.0.1/")]
    [DataRow("file:///etc/passwd")]
    public void IsSafePublicHttpUrl_RejectsPrivateOrNonHttpTargets(string url)
    {
        Assert.IsFalse(UrlSafety.IsSafePublicHttpUrl(url, out var reason));
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
    }

    [TestMethod]
    public void IsSafePublicHttpUrl_AllowsPublicHttpsHost()
    {
        Assert.IsTrue(UrlSafety.IsSafePublicHttpUrl("https://example.com/feed.xml", out var reason));
        Assert.IsNull(reason);
    }

    [TestMethod]
    public void IsPublicIpAddress_RejectsLoopbackAndLinkLocal()
    {
        Assert.IsFalse(UrlSafety.IsPublicIpAddress(IPAddress.Parse("127.0.0.1")));
        Assert.IsFalse(UrlSafety.IsPublicIpAddress(IPAddress.Parse("169.254.169.254")));
        Assert.IsTrue(UrlSafety.IsPublicIpAddress(IPAddress.Parse("8.8.8.8")));
    }
}
