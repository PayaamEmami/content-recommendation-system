using System.Net;
using Crs.Tests.Infrastructure;

namespace Crs.Tests.Integration;

[TestClass]
public sealed class VectorStoreHealthIntegrationTests
{
    private static ApiWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        _factory = new ApiWebApplicationFactory();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _factory.Dispose();
    }

    [TestInitialize]
    public void TestInitialize()
    {
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _client.Dispose();
    }

    [TestMethod]
    public async Task HealthReady_IncludesHealthyVectorStoreCheck()
    {
        var response = await _client.GetAsync("/health/ready");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "vector-store");
        StringAssert.Contains(body, "Vector store is reachable");
        StringAssert.Contains(body, "documentCount");
    }
}
