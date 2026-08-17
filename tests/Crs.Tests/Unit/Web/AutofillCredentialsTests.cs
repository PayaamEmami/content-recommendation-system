using Crs.Web.Services;

namespace Crs.Tests.Unit.Web;

[TestClass]
public sealed class AutofillCredentialsTests
{
    [TestMethod]
    public void PreferDom_UsesDomPasswordWhenModelIsStale()
    {
        var (email, password) = AutofillCredentials.PreferDom(
            "user@example.com",
            "partial",
            "user@example.com",
            "correct-password");

        Assert.AreEqual("user@example.com", email);
        Assert.AreEqual("correct-password", password);
    }

    [TestMethod]
    public void PreferDom_KeepsModelWhenDomIsEmpty()
    {
        var (email, password) = AutofillCredentials.PreferDom(
            "user@example.com",
            "typed-password",
            "",
            "");

        Assert.AreEqual("user@example.com", email);
        Assert.AreEqual("typed-password", password);
    }

    [TestMethod]
    public void PreferDom_TrimsDomEmail()
    {
        var (email, password) = AutofillCredentials.PreferDom(
            "",
            "",
            "  user@example.com  ",
            "secret");

        Assert.AreEqual("user@example.com", email);
        Assert.AreEqual("secret", password);
    }
}
