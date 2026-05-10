using Crs.Core.Enums;

namespace Crs.Web.Services;

public sealed class DevelopmentDataStore
{
    private static readonly Guid DevUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid PaperId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid VideoId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid BlogId = Guid.Parse("10000000-0000-0000-0000-000000000003");

    private readonly List<ContentItem> _content;
    private readonly List<SourceItem> _sources;
    private readonly List<PreferenceItem> _preferences;
    private readonly List<XFollowedAccountItem> _followedAccounts;
    private readonly List<XPostItem> _xPosts;
    private readonly Dictionary<Guid, VoteType> _votes = new()
    {
        [PaperId] = VoteType.Upvote,
        [BlogId] = VoteType.Downvote
    };

    public DevelopmentDataStore()
    {
        var now = DateTime.UtcNow;

        _content =
        [
            new()
            {
                Id = PaperId,
                Title = "Attention Is All You Need",
                Url = "https://arxiv.org/abs/1706.03762",
                Type = ContentType.Paper,
                Description = "A classic transformer paper used here as seeded local development content.",
                PublishedAt = now.AddDays(-1)
            },
            new()
            {
                Id = VideoId,
                Title = "Neural Networks from Scratch",
                Url = "https://www.youtube.com/watch?v=aircAruvnKk",
                Type = ContentType.Video,
                Description = "A representative video recommendation for local UI testing.",
                PublishedAt = now.AddDays(-3)
            },
            new()
            {
                Id = BlogId,
                Title = "Building Better Recommendation Systems",
                Url = "https://example.com/recommendation-systems",
                Type = ContentType.BlogPost,
                Description = "Example blog content with enough text to exercise feed row wrapping.",
                PublishedAt = now.AddDays(-9)
            }
        ];

        _sources =
        [
            new()
            {
                Id = Guid.Parse("20000000-0000-0000-0000-000000000001"),
                UserId = DevUserId,
                Name = "ArXiv AI",
                Url = "https://arxiv.org/list/cs.AI/recent",
                Category = ContentType.Paper,
                Description = "Recent AI papers",
                IsActive = true,
                CreatedAt = now.AddDays(-30),
                LastFetchedAt = now.AddHours(-6),
                ContentCount = 18
            },
            new()
            {
                Id = Guid.Parse("20000000-0000-0000-0000-000000000002"),
                UserId = DevUserId,
                Name = "3Blue1Brown",
                Url = "https://youtube.com/c/3blue1brown",
                Category = ContentType.Video,
                Description = "Math and ML explainer videos",
                IsActive = true,
                CreatedAt = now.AddDays(-20),
                LastFetchedAt = now.AddDays(-1),
                ContentCount = 7
            }
        ];

        _preferences =
        [
            new()
            {
                Id = Guid.Parse("30000000-0000-0000-0000-000000000001"),
                UserId = DevUserId,
                Title = "Distill: The Building Blocks of Interpretability",
                Url = "https://distill.pub/2018/building-blocks/",
                Description = "Seeded upvote example for local preference UI testing.",
                ContentType = ContentType.BlogPost,
                VoteType = VoteType.Upvote,
                CreatedAt = now.AddDays(-12),
                UpdatedAt = now.AddDays(-4)
            }
        ];

        _followedAccounts =
        [
            new()
            {
                Id = Guid.Parse("40000000-0000-0000-0000-000000000001"),
                XUserId = "dev-001",
                Handle = "local_recs",
                DisplayName = "Local Recs",
                ProfileImageUrl = "https://placehold.co/64x64/png",
                IsSelected = true
            },
            new()
            {
                Id = Guid.Parse("40000000-0000-0000-0000-000000000002"),
                XUserId = "dev-002",
                Handle = "ml_notes",
                DisplayName = "ML Notes",
                ProfileImageUrl = "https://placehold.co/64x64/png",
                IsSelected = true
            }
        ];

        _xPosts =
        [
            new()
            {
                Id = Guid.Parse("50000000-0000-0000-0000-000000000001"),
                PostId = "dev-post-1",
                Text = "Local development post: a new ranking experiment improved freshness without hurting relevance.",
                Url = "https://x.com/local_recs/status/dev-post-1",
                PostCreatedAt = now.AddHours(-2),
                AuthorHandle = "local_recs",
                AuthorName = "Local Recs",
                AuthorProfileImageUrl = "https://placehold.co/64x64/png",
                LikeCount = 42,
                ReplyCount = 3,
                RepostCount = 8,
                QuoteCount = 1
            },
            new()
            {
                Id = Guid.Parse("50000000-0000-0000-0000-000000000002"),
                PostId = "dev-post-2",
                Text = "Synthetic social content lets the feed layout be tested without connecting a real X account.",
                Url = "https://x.com/ml_notes/status/dev-post-2",
                PostCreatedAt = now.AddHours(-8),
                AuthorHandle = "ml_notes",
                AuthorName = "ML Notes",
                AuthorProfileImageUrl = "https://placehold.co/64x64/png",
                LikeCount = 128,
                ReplyCount = 11,
                RepostCount = 19,
                QuoteCount = 2
            }
        ];
    }

    public List<ContentItem> GetFeed(ContentType? type = null)
    {
        return _content
            .Where(item => !type.HasValue || item.Type == type.Value)
            .OrderByDescending(item => item.PublishedAt)
            .Select(Clone)
            .ToList();
    }

    public List<VoteItem> GetVotes()
    {
        return _votes.Select(pair => new VoteItem
        {
            Id = Guid.NewGuid(),
            UserId = DevUserId,
            ContentId = pair.Key,
            VoteType = pair.Value,
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            UpdatedAt = DateTime.UtcNow
        }).ToList();
    }

    public List<VoteHistoryItem> GetVoteHistory()
    {
        return _content.Select(item => new VoteHistoryItem
        {
            Id = Guid.NewGuid(),
            UserId = DevUserId,
            ContentId = item.Id,
            VoteType = _votes.TryGetValue(item.Id, out var voteType) ? voteType : null,
            VoteCreatedAt = DateTime.UtcNow.AddDays(-3),
            VoteUpdatedAt = DateTime.UtcNow,
            Title = item.Title,
            Description = item.Description,
            Url = item.Url,
            Type = item.Type,
            ContentDate = item.PublishedAt
        }).ToList();
    }

    public VoteItem Vote(Guid contentId, VoteType voteType)
    {
        _votes[contentId] = voteType;
        return new VoteItem
        {
            Id = Guid.NewGuid(),
            UserId = DevUserId,
            ContentId = contentId,
            VoteType = voteType,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public bool RemoveVote(Guid contentId)
    {
        _votes.Remove(contentId);
        return true;
    }

    public SourceLoadResult GetSources()
    {
        return SourceLoadResult.Success(_sources.OrderBy(source => source.Name).Select(Clone).ToList());
    }

    public bool AddSource(string name, string url, ContentType category, string? description)
    {
        _sources.Add(new SourceItem
        {
            Id = Guid.NewGuid(),
            UserId = DevUserId,
            Name = name,
            Url = url,
            Category = category,
            Description = description,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ContentCount = 0
        });
        return true;
    }

    public bool UpdateSource(Guid sourceId, string name, string url, ContentType category, string? description)
    {
        var source = _sources.FirstOrDefault(item => item.Id == sourceId);
        if (source == null)
        {
            return false;
        }

        source.Name = name;
        source.Url = url;
        source.Category = category;
        source.Description = description;
        return true;
    }

    public bool ToggleSource(Guid sourceId)
    {
        var source = _sources.FirstOrDefault(item => item.Id == sourceId);
        if (source == null)
        {
            return false;
        }

        source.IsActive = !source.IsActive;
        return true;
    }

    public bool DeleteSource(Guid sourceId)
    {
        return _sources.RemoveAll(item => item.Id == sourceId) > 0;
    }

    public ImportResultModel ImportSources(string json)
    {
        return new ImportResultModel
        {
            Imported = string.IsNullOrWhiteSpace(json) ? 0 : 1,
            Failed = 0
        };
    }

    public List<PreferenceItem> GetPreferences()
    {
        return _preferences.Select(Clone).ToList();
    }

    public PreferenceItem CreatePreference(PreferenceUpsertRequest request)
    {
        var item = new PreferenceItem
        {
            Id = Guid.NewGuid(),
            UserId = DevUserId,
            Title = request.Title,
            Description = request.Description,
            Url = request.Url,
            ContentType = request.ContentType,
            VoteType = request.VoteType,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _preferences.Add(item);
        return Clone(item);
    }

    public PreferenceItem? UpdatePreference(Guid id, PreferenceUpsertRequest request)
    {
        var item = _preferences.FirstOrDefault(preference => preference.Id == id);
        if (item == null)
        {
            return null;
        }

        item.Title = request.Title;
        item.Description = request.Description;
        item.Url = request.Url;
        item.ContentType = request.ContentType;
        item.VoteType = request.VoteType;
        item.UpdatedAt = DateTime.UtcNow;
        return Clone(item);
    }

    public bool DeletePreference(Guid id)
    {
        return _preferences.RemoveAll(preference => preference.Id == id) > 0;
    }

    public List<XFollowedAccountItem> GetFollowedAccounts()
    {
        return _followedAccounts.Select(Clone).ToList();
    }

    public List<XFollowedAccountItem> UpdateSelectedAccounts(List<Guid> followedAccountIds)
    {
        foreach (var account in _followedAccounts)
        {
            account.IsSelected = followedAccountIds.Contains(account.Id);
        }

        return GetFollowedAccounts();
    }

    public List<XPostItem> GetPosts(int limit)
    {
        return _xPosts
            .Where(post => _followedAccounts.Any(account => account.IsSelected && account.Handle == post.AuthorHandle))
            .Take(limit)
            .Select(Clone)
            .ToList();
    }

    private static ContentItem Clone(ContentItem item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        Url = item.Url,
        Type = item.Type,
        Description = item.Description,
        PublishedAt = item.PublishedAt
    };

    private static SourceItem Clone(SourceItem item) => new()
    {
        Id = item.Id,
        UserId = item.UserId,
        Name = item.Name,
        Url = item.Url,
        Category = item.Category,
        Description = item.Description,
        IsActive = item.IsActive,
        CreatedAt = item.CreatedAt,
        LastFetchedAt = item.LastFetchedAt,
        ContentCount = item.ContentCount
    };

    private static PreferenceItem Clone(PreferenceItem item) => new()
    {
        Id = item.Id,
        UserId = item.UserId,
        Title = item.Title,
        Description = item.Description,
        Url = item.Url,
        ContentType = item.ContentType,
        VoteType = item.VoteType,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt
    };

    private static XFollowedAccountItem Clone(XFollowedAccountItem item) => new()
    {
        Id = item.Id,
        XUserId = item.XUserId,
        Handle = item.Handle,
        DisplayName = item.DisplayName,
        ProfileImageUrl = item.ProfileImageUrl,
        IsSelected = item.IsSelected
    };

    private static XPostItem Clone(XPostItem item) => new()
    {
        Id = item.Id,
        PostId = item.PostId,
        Text = item.Text,
        Url = item.Url,
        PostCreatedAt = item.PostCreatedAt,
        AuthorHandle = item.AuthorHandle,
        AuthorName = item.AuthorName,
        AuthorProfileImageUrl = item.AuthorProfileImageUrl,
        MediaJson = item.MediaJson,
        LikeCount = item.LikeCount,
        ReplyCount = item.ReplyCount,
        RepostCount = item.RepostCount,
        QuoteCount = item.QuoteCount
    };
}

