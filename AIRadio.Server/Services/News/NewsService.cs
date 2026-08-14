namespace AIRadio.Server.Services.News
{
    public interface INewsService
    {
        Task<NewsResult> GetHeadlinesAsync(
            NewsQuery query,
            CancellationToken cancellationToken = default);
    }

    public sealed class NewsService : INewsService
    {
        private readonly INewsProvider _provider;
        private readonly ILogger<NewsService> _logger;

        public NewsService(
            INewsProvider provider,
            ILogger<NewsService> logger)
        {
            _provider = provider;
            _logger = logger;
        }

        public async Task<NewsResult> GetHeadlinesAsync(
            NewsQuery query,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);

            var limit = Math.Clamp(
                query.Limit,
                1,
                20);

            var normalizedQuery = new NewsQuery
            {
                Category = query.Category,
                Location = query.Location,
                Limit = limit,
                MaxAge = query.MaxAge
            };

            _logger.LogDebug(
                "Getting news. Category={Category}, Location={Location}, Limit={Limit}.",
                normalizedQuery.Category,
                normalizedQuery.Location,
                normalizedQuery.Limit);

            var articles =
                await _provider.GetArticlesAsync(
                    normalizedQuery,
                    cancellationToken);

            return new NewsResult
            {
                RetrievedAt = DateTimeOffset.Now,
                Category = normalizedQuery.Category,
                Location = normalizedQuery.Location,
                Articles = articles
                    .Take(limit)
                    .ToList()
            };
        }
    }

    public sealed class NewsQuery
    {
        /// <summary>
        /// Optional news category.
        /// Examples: top, world, technology, sports, business.
        /// </summary>
        public string? Category { get; init; }

        /// <summary>
        /// Optional geographic or source location.
        /// </summary>
        public string? Location { get; init; }

        /// <summary>
        /// Maximum number of articles to return.
        /// </summary>
        public int Limit { get; init; } = 5;

        /// <summary>
        /// Only return articles published within this period.
        /// </summary>
        public TimeSpan? MaxAge { get; init; }
    }

    public sealed class NewsArticle
    {
        /// <summary>
        /// Unique identifier for the article, if available.
        /// </summary>
        public string? Id { get; init; }

        /// <summary>
        /// Article headline.
        /// </summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>
        /// Short description or summary.
        /// </summary>
        public string? Summary { get; init; }

        /// <summary>
        /// News source name.
        /// </summary>
        public string? Source { get; init; }

        /// <summary>
        /// Publication timestamp.
        /// </summary>
        public DateTimeOffset? PublishedAt { get; init; }

        /// <summary>
        /// URL of the original article.
        /// </summary>
        public string? Url { get; init; }

        /// <summary>
        /// Category assigned to the article.
        /// </summary>
        public string? Category { get; init; }
    }

    public sealed class NewsResult
    {
        public DateTimeOffset RetrievedAt { get; init; }

        public string? Category { get; init; }

        public string? Location { get; init; }

        public IReadOnlyList<NewsArticle> Articles { get; init; } =
            [];
    }
}
