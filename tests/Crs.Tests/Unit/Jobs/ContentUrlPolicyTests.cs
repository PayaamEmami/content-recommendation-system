using Crs.Core.Enums;
using Crs.Llm.Validation;

namespace Crs.Tests.Unit.Jobs;

[TestClass]
public sealed class ContentUrlPolicyTests
{
    [TestMethod]
    [DataRow("https://www.deeplearning.ai/the-batch/tag/jan-30-2026/")]
    [DataRow("https://example.com/category/machine-learning/")]
    [DataRow("https://example.com/2026/01/30/")]
    [DataRow("https://example.com/blog?page=2")]
    public void IsLikelyContentUrl_ForBlogPost_NonContentPagesAreRejected(string url)
    {
        var result = ContentUrlPolicy.IsLikelyContentUrl(url, ContentType.BlogPost, "https://example.com");

        Assert.IsFalse(result);
    }

    [TestMethod]
    [DataRow("https://www.deeplearning.ai/the-batch/some-actual-post/")]
    [DataRow("https://example.com/posts/how-transformers-work")]
    [DataRow("https://example.com/2026/01/30/post-title")]
    public void IsLikelyContentUrl_ForBlogPost_RealPostsAreAccepted(string url)
    {
        var result = ContentUrlPolicy.IsLikelyContentUrl(url, ContentType.BlogPost, "https://example.com");

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void IsLikelyContentUrl_SourceRootUrlIsRejected()
    {
        var result = ContentUrlPolicy.IsLikelyContentUrl(
            "https://example.com/feed",
            ContentType.BlogPost,
            "https://example.com/feed");

        Assert.IsFalse(result);
    }
}
