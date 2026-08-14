using System.Xml;
using System.ServiceModel.Syndication;

namespace AIRadio.Server.Services.News
{
    public interface INewsProvider
    {
        Task<IReadOnlyList<NewsArticle>> GetArticlesAsync(
            NewsQuery query,
            CancellationToken cancellationToken = default);
    }

    public sealed class RssNewsProvider : INewsProvider
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<RssNewsProvider> _logger;

        public RssNewsProvider(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<RssNewsProvider> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<IReadOnlyList<NewsArticle>> GetArticlesAsync(
            NewsQuery query,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);

            var feeds = GetFeeds(query);

            if (feeds.Count == 0)
            {
                _logger.LogWarning(
                    "No news feeds are configured.");

                return [];
            }

            var articles = new List<NewsArticle>();

            foreach (var feed in feeds)
            {
                try
                {
                    var feedArticles =
                        await ReadFeedAsync(
                            feed,
                            query,
                            cancellationToken);

                    articles.AddRange(feedArticles);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Unable to read news feed {FeedName} ({Url}).",
                        feed.Name,
                        feed.Url);
                }
            }

            return articles
                .Where(article =>
                    !query.MaxAge.HasValue ||
                    !article.PublishedAt.HasValue ||
                    article.PublishedAt.Value >=
                        DateTimeOffset.Now.Subtract(
                            query.MaxAge.Value))
                .OrderByDescending(
                    article => article.PublishedAt)
                .Take(Math.Clamp(
                    query.Limit,
                    1,
                    20))
                .ToList();
        }

        private async Task<IReadOnlyList<NewsArticle>> ReadFeedAsync(
            NewsFeed feed,
            NewsQuery query,
            CancellationToken cancellationToken)
        {
            using var stream =
                await _httpClient.GetStreamAsync(
                    feed.Url,
                    cancellationToken);

            using var reader =
                XmlReader.Create(
                    stream,
                    new XmlReaderSettings
                    {
                        Async = true,
                        DtdProcessing =
                            DtdProcessing.Ignore
                    });

            var syndicationFeed =
                SyndicationFeed.Load(reader);

            var articles = new List<NewsArticle>();

            foreach (var item in syndicationFeed.Items)
            {
                var published =
                    GetPublishedDate(item);

                articles.Add(
                    new NewsArticle
                    {
                        Id =
                            GetArticleId(item),

                        Title =
                            item.Title?.Text?.Trim()
                            ?? string.Empty,

                        Summary =
                            GetSummary(item),

                        Source =
                            feed.Name,

                        PublishedAt =
                            published,

                        Url =
                            item.Links
                                .FirstOrDefault()?
                                .Uri?
                                .ToString(),

                        Category =
                            query.Category
                    });
            }

            return articles;
        }

        private static DateTimeOffset? GetPublishedDate(
            SyndicationItem item)
        {
            if (item.PublishDate != default)
            {
                return item.PublishDate;
            }

            if (item.LastUpdatedTime != default)
            {
                return item.LastUpdatedTime;
            }

            return null;
        }

        private static string? GetArticleId(
            SyndicationItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.Id))
            {
                return item.Id;
            }

            return item.Links
                .FirstOrDefault()?
                .Uri?
                .ToString();
        }

        private static string? GetSummary(
            SyndicationItem item)
        {
            var summary =
                item.Summary?.Text;

            if (string.IsNullOrWhiteSpace(summary))
            {
                return null;
            }

            return StripHtml(summary).Trim();
        }

        private static string StripHtml(
            string value)
        {
            return System.Net.WebUtility
                .HtmlDecode(
                    System.Text.RegularExpressions.Regex
                        .Replace(
                            value,
                            "<.*?>",
                            string.Empty));
        }

        private List<NewsFeed> GetFeeds(
            NewsQuery query)
        {
            var feeds =
                _configuration
                    .GetSection("News:Feeds")
                    .Get<List<NewsFeed>>()
                ?? [];

            if (string.IsNullOrWhiteSpace(
                    query.Category))
            {
                return feeds;
            }

            var category =
                query.Category.Trim();

            var categorized =
                feeds
                    .Where(feed =>
                        feed.Categories.Count == 0 ||
                        feed.Categories.Contains(
                            category,
                            StringComparer.OrdinalIgnoreCase))
                    .ToList();

            return categorized;
        }
    }

    public sealed class NewsFeed
    {
        public string Name { get; set; } =
            string.Empty;

        public string Url { get; set; } =
            string.Empty;

        public List<string> Categories { get; set; } =
            [];
    }
}
