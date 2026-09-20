namespace AIRadio.Server.Services.News
{
    public interface INewsService
    {
        Task<NewsResult> GetHeadlinesAsync(
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
            CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Getting top national news headlines.");

            var articles =
                await _provider.GetArticlesAsync(
                    cancellationToken);

            return new NewsResult
            {
                RetrievedAt = DateTimeOffset.Now,
                Articles = articles
            };
        }
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
    }

    public sealed class NewsResult
    {
        public DateTimeOffset RetrievedAt { get; init; }

        public IReadOnlyList<NewsArticle> Articles { get; init; } =
            [];
    }
}
