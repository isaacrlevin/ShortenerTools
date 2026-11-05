using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using ShortenerTools.Core.Messages;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace ShortenerTools.Core.Domain
{
    public class StorageTableHelper
    {
        private string StorageConnectionString { get; set; }
        private readonly TableServiceClient? _tableServiceClient;

        /// <summary>Optional logger for query timings; nothing is logged when it is not set.</summary>
        public ILogger? Logger { get; init; }

        public StorageTableHelper()
        {
        }

        public StorageTableHelper(string storageConnectionString)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(storageConnectionString);
            StorageConnectionString = storageConnectionString;
            if (Uri.TryCreate(storageConnectionString, UriKind.Absolute, out var tableEndpoint)
                && tableEndpoint.Scheme is "https" or "http")
            {
                _tableServiceClient = new TableServiceClient(tableEndpoint, new DefaultAzureCredential());
            }
        }

        /// <summary>Creates a helper using an existing Azure Tables client.</summary>
        /// <param name="tableServiceClient">The client used for table operations.</param>
        public StorageTableHelper(TableServiceClient tableServiceClient)
        {
            ArgumentNullException.ThrowIfNull(tableServiceClient);
            _tableServiceClient = tableServiceClient;
        }

        public TableServiceClient CreateStorageAccountFromConnectionString()
        {
            TableServiceClient tableClient = _tableServiceClient ?? new TableServiceClient(StorageConnectionString);
            return tableClient;
        }

        private TableClient GetTable(string tableName)
        {
            TableServiceClient tableClient = CreateStorageAccountFromConnectionString();
            TableClient table = tableClient.GetTableClient(tableName);
            table.CreateIfNotExists();

            return table;
        }

        private TableClient GetUrlsTable()
        {
            TableClient table = GetTable("UrlsDetails");
            return table;
        }

        private TableClient GetStatsTable()
        {
            TableClient table = GetTable("ClickStats");
            return table;
        }

        private TableClient GetPostVersionsTable()
        {
            TableClient table = GetTable("PostVersions");
            return table;
        }

        /// <summary>Appends a new post version; existing versions are never modified.</summary>
        public virtual async Task<PostVersionEntity> AddPostVersion(PostVersionEntity version)
        {
            var tableClient = GetPostVersionsTable();
            await tableClient.AddEntityAsync(version);
            return version;
        }

        /// <summary>Records the outcome of publishing a version; the text itself is never modified.</summary>
        public virtual async Task MarkPostVersion(PostVersionEntity version, string status, string? externalPostId = null)
        {
            if (version.IsLegacy) return;
            version.Status = status;
            version.ExternalPostId = externalPostId ?? version.ExternalPostId;
            await GetPostVersionsTable().UpdateEntityAsync(version, version.ETag, TableUpdateMode.Replace);
        }

        /// <summary>
        /// Returns versions for a link, newest first within each account. Links created before versioning
        /// have no rows, so a single synthesized version is returned from the original record.
        /// </summary>
        /// <param name="shortcode">The link's shortcode.</param>
        /// <param name="account">Optional account to restrict the result to.</param>
        public virtual async Task<List<PostVersionEntity>> GetPostVersions(string shortcode, string? account = null)
        {
            var tableClient = GetPostVersionsTable();
            var filter = TableClient.CreateQueryFilter($"PartitionKey eq {shortcode}");
            if (!string.IsNullOrWhiteSpace(account))
            {
                // '}' sorts immediately after '|', which bounds the "{account}|" prefix range.
                var prefix = PostVersionEntity.SanitizeAccount(account) + PostVersionEntity.Separator;
                var upper = PostVersionEntity.SanitizeAccount(account) + "}";
                filter += " and " + TableClient.CreateQueryFilter($"RowKey ge {prefix} and RowKey lt {upper}");
            }

            var versions = new List<PostVersionEntity>();
            await foreach (var version in tableClient.QueryAsync<PostVersionEntity>(filter))
                versions.Add(version);
            if (versions.Count > 0) return versions;

            if (!string.IsNullOrWhiteSpace(account) && !account.Equals("legacy", StringComparison.OrdinalIgnoreCase))
                return versions;
            var link = await GetShortUrlEntity(new ShortUrlEntity(string.Empty, shortcode));
            return link is null || string.IsNullOrEmpty(link.Message)
                ? versions
                : [PostVersionEntity.FromLegacy(link)];
        }

        /// <summary>Returns the most recent version for one account, or null when it has none.</summary>
        public virtual async Task<PostVersionEntity?> GetLatestPostVersion(string shortcode, string account) =>
            (await GetPostVersions(shortcode, account)).FirstOrDefault();

        public virtual async Task<ShortUrlEntity?> GetShortUrlEntity(ShortUrlEntity row)
        {
            var tableClient = GetUrlsTable();
            var result = await tableClient.GetEntityIfExistsAsync<ShortUrlEntity>(row.PartitionKey, row.RowKey);
            return (result.HasValue == true ? result.Value : null);
        }

        public virtual async Task<List<ShortUrlEntity>> GetAllShortUrlEntities()
        {
            var tableClient = GetUrlsTable();
            List<ShortUrlEntity> lstShortUrl = new List<ShortUrlEntity>();
            Pageable<ShortUrlEntity> queryResultsLINQ =
                tableClient.Query<ShortUrlEntity>(ent => !ent.IsArchived && ent.RowKey != "KEY");

            foreach (ShortUrlEntity qEntity in queryResultsLINQ)
            {
                //qEntity.ClickCount = stats.Where(a => a.PartitionKey == qEntity.RowKey).Count();
                lstShortUrl.AddRange(qEntity);
            }

            return lstShortUrl;
        }

        public async Task<(List<ShortUrlEntity> Items, int TotalCount)> GetShortUrlEntitiesPaged(int skip, int take)
        {
            var result = await GetShortUrlEntitiesPaged(new UrlListQuery { Skip = skip, Take = take });
            return (result.UrlList, result.TotalCount);
        }

        /// <summary>Returns a filtered and sorted page of active links.</summary>
        /// <param name="query">Paging, sorting, and filtering options.</param>
        /// <returns>The requested links and matching count.</returns>
        public async Task<ListResponse> GetShortUrlEntitiesPaged(UrlListQuery query)
        {
            query.Validate();
            var tableClient = GetUrlsTable();
            var items = new List<ShortUrlEntity>();
            // Table Storage orders by keys only; arbitrary column sorts require a server-side scan.
            await foreach (var item in tableClient.QueryAsync<ShortUrlEntity>(ent =>
                               !ent.IsArchived && ent.RowKey != "KEY"))
                items.Add(item);
            return query.Apply(items);
        }

        /// <summary>
        /// Returns the ShortUrlEntity of the <paramref name="vanity"/>
        /// </summary>
        /// <param name="vanity"></param>
        /// <returns>ShortUrlEntity</returns>
        public async Task<ShortUrlEntity> GetShortUrlEntityByVanity(string vanity)
        {
            var tableClient = GetUrlsTable();
            Pageable<ShortUrlEntity> results = tableClient.Query<ShortUrlEntity>(ent => ent.ShortUrl == vanity);
            return results.FirstOrDefault();
        }

        /// <summary>Returns the active link created by <paramref name="creationId"/>, if a previous attempt made one.</summary>
        public virtual async Task<ShortUrlEntity?> GetShortUrlEntityByCreationId(string creationId)
        {
            var tableClient = GetUrlsTable();
            await foreach (var item in tableClient.QueryAsync<ShortUrlEntity>(ent =>
                               ent.CreationId == creationId && !ent.IsArchived))
                return item;
            return null;
        }

        public virtual async Task SaveClickStatsEntity(ClickStatsEntity newStats)
        {
            var tableClient = GetStatsTable();
            await tableClient.UpsertEntityAsync(newStats);
        }

        /// <summary>Creates a link without overwriting any existing or archived shortcode.</summary>
        public async Task<bool> TryCreateShortUrlEntity(ShortUrlEntity newShortUrl)
        {
            var tableClient = GetUrlsTable();
            try
            {
                await tableClient.AddEntityAsync(newShortUrl);
                return true;
            }
            catch (RequestFailedException exception) when (exception.Status == 409)
            {
                return false;
            }
        }

        /// <summary>Returns the original record when retrying an identified creation; never updates its metadata.</summary>
        public virtual async Task<ShortUrlEntity?> CreateOrGetShortUrlEntity(ShortUrlEntity candidate)
        {
            if (await TryCreateShortUrlEntity(candidate)) return candidate;
            if (string.IsNullOrEmpty(candidate.CreationId)) return null;
            var existing = await GetShortUrlEntity(candidate);
            return existing is not null && !existing.IsArchived && existing.CreationId == candidate.CreationId
                   && existing.Url == candidate.Url
                ? existing
                : null;
        }

        public async Task<ShortUrlEntity> SaveShortUrlEntity(ShortUrlEntity newShortUrl)
        {
            var tableClient = GetUrlsTable();
            await tableClient.UpsertEntityAsync(newShortUrl);
            return newShortUrl;
        }

        public virtual async Task<bool> IfShortUrlEntityExistByVanity(string vanity)
        {
            ShortUrlEntity shortUrlEntity = await GetShortUrlEntityByVanity(vanity);
            return (shortUrlEntity != null);
        }

        public async Task<bool> IfShortUrlEntityExist(ShortUrlEntity row)
        {
            ShortUrlEntity eShortUrl = await GetShortUrlEntity(row);
            return (eShortUrl != null);
        }

        public virtual async Task<int> GetNextTableId()
        {
            var tableClient = GetUrlsTable();


            var result = await tableClient.GetEntityIfExistsAsync<NextId>("1", "KEY");

            NextId entity = null;

            if (result.HasValue)
            {
                entity = result.Value as NextId;
            }
            else
            {
                entity = new NextId
                {
                    PartitionKey = "1",
                    RowKey = "KEY",
                    Id = 1024
                };
            }

            entity.Id++;

            tableClient.UpsertEntity(entity);

            ShortUrlEntity updatedEntity =
                await tableClient.GetEntityAsync<ShortUrlEntity>(entity.PartitionKey, entity.RowKey);

            return entity.Id;
        }


        public virtual async Task<ShortUrlEntity> UpdateShortUrlEntity(ShortUrlEntity urlEntity)
        {
            var originalUrl = await GetShortUrlEntity(urlEntity) ??
                              throw new InvalidOperationException("The link no longer exists.");
            originalUrl.Url = urlEntity.Url;
            originalUrl.Title = urlEntity.Title;
            originalUrl.Message = urlEntity.Message;
            originalUrl.Posted = urlEntity.Posted;

            return await SaveShortUrlEntity(originalUrl);
        }


        // Table Storage only indexes PartitionKey and RowKey, so a Timestamp (or any column) filter still scans every
        // row. ClickStats rows are keyed PartitionKey = vanity and RowKey = UTC ticks of the click, so a RowKey range
        // inside one partition is an indexed read. The margin covers display time zones (and legacy local Datetime
        // values); exact date filtering then uses Datetime, exactly like the daily chart.
        private static readonly TimeSpan StatsQueryMargin = TimeSpan.FromDays(2);

        // RowKeys are unpadded tick strings, so they only sort chronologically while they all have 18 digits
        // (years 0317-3169). Bounds outside that range are dropped, which just means a wider (still correct) read.
        private const long MinTickRowKey = 100_000_000_000_000_000L;
        private const long MaxTickRowKey = 1_000_000_000_000_000_000L;
        // Each per-link query is a ~30 ms round trip returning few rows, so with thousands of links the wall time is
        // links / concurrency * latency. Measured against production: 16 -> 7,567 links in ~13.5 s.
        private const int ClickPartitionConcurrency = 128;

        /// <summary>Returns the inclusive-lower / exclusive-upper RowKey tick bounds for a request, when it has dates.</summary>
        internal static (string? Lower, string? Upper) GetClickRowKeyBounds(UrlClickStatsRequest request)
        {
            string? lower = null, upper = null;
            if (DateTime.TryParseExact(request.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var start))
            {
                var ticks = start.Ticks - StatsQueryMargin.Ticks;
                if (ticks is >= MinTickRowKey and < MaxTickRowKey)
                    lower = ticks.ToString(CultureInfo.InvariantCulture);
            }

            if (DateTime.TryParseExact(request.EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var end))
            {
                // The end date is inclusive, so bound at the start of the following day.
                var ticks = end.Ticks + TimeSpan.TicksPerDay + StatsQueryMargin.Ticks;
                if (ticks is >= MinTickRowKey and < MaxTickRowKey)
                    upper = ticks.ToString(CultureInfo.InvariantCulture);
            }

            return (lower, upper);
        }

        /// <summary>
        /// Builds the server-side filter for one click partition: the vanity, plus a RowKey (click time) range when the
        /// request has dates. Without a vanity there is no key to range over, so the filter is null (full scan).
        /// </summary>
        internal static string? BuildStatsFilter(UrlClickStatsRequest request, string? vanity = null)
        {
            vanity = string.IsNullOrEmpty(vanity) ? request.Vanity : vanity;
            if (string.IsNullOrEmpty(vanity))
                return null;

            var filter = TableClient.CreateQueryFilter($"PartitionKey eq {vanity}");
            var (lower, upper) = GetClickRowKeyBounds(request);
            if (lower is not null)
                filter += " and " + TableClient.CreateQueryFilter($"RowKey ge {lower}");
            if (upper is not null)
                filter += " and " + TableClient.CreateQueryFilter($"RowKey lt {upper}");
            return filter;
        }

        /// <summary>Lists every link code (archived included) - each is a ClickStats partition.</summary>
        private async Task<List<string>> GetClickPartitions()
        {
            var partitions = new List<string>();
            await foreach (var link in GetUrlsTable().QueryAsync<TableEntity>(
                               filter: TableClient.CreateQueryFilter($"RowKey ne {"KEY"}"), select: new[] { "RowKey" }))
                partitions.Add(link.RowKey);
            return partitions;
        }

        /// <summary>
        /// Streams the click rows a request can match. One vanity is a single partition query; all links with dates fan
        /// out to one RowKey-range query per link, so the cost follows the date range instead of the table size.
        /// </summary>
        private async IAsyncEnumerable<T> QueryClicks<T>(UrlClickStatsRequest request, string[]? select,
            [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class, ITableEntity
        {
            var table = GetStatsTable();
            var (lower, upper) = GetClickRowKeyBounds(request);
            if (!string.IsNullOrEmpty(request.Vanity) || (lower is null && upper is null))
            {
                await foreach (var row in table.QueryAsync<T>(filter: BuildStatsFilter(request), select: select,
                                   cancellationToken: cancellationToken))
                    yield return row;
                yield break;
            }

            var listing = Stopwatch.StartNew();
            var partitions = await GetClickPartitions();
            Logger?.LogInformation("ClickStats fan-out: {Partitions} link partitions listed in {ElapsedMs} ms.",
                partitions.Count, listing.ElapsedMilliseconds);
            var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(2000) { SingleReader = true });
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var producer = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(partitions,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = ClickPartitionConcurrency,
                            CancellationToken = stop.Token
                        },
                        async (vanity, token) =>
                        {
                            await foreach (var row in table.QueryAsync<T>(filter: BuildStatsFilter(request, vanity),
                                               select: select, cancellationToken: token))
                                await channel.Writer.WriteAsync(row, token);
                        });
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            });

            try
            {
                await foreach (var row in channel.Reader.ReadAllAsync(cancellationToken))
                    yield return row;
            }
            finally
            {
                // Stops the producers if the caller abandons the enumeration or a query fails.
                stop.Cancel();
                await producer;
            }
        }

        /// <summary>Returns click records for the request's vanity and inclusive local date range, newest first.</summary>
        /// <exception cref="ArgumentException">The dates or time zone are invalid.</exception>
        public virtual async Task<List<ClickStatsEntity>> GetStats(UrlClickStatsRequest request)
        {
            var range = new DailyClickAggregation(request);
            var stats = new List<ClickStatsEntity>();
            var timer = Stopwatch.StartNew();
            var read = 0;
            await foreach (var click in QueryClicks<ClickStatsEntity>(request, null))
            {
                read++;
                if (range.Includes(click.Datetime))
                    stats.Add(click);
            }

            Logger?.LogInformation(
                "GetStats: read {Read} rows, kept {Kept}, in {ElapsedMs} ms (vanity: {Vanity}, {Start} to {End}).",
                read, stats.Count, timer.ElapsedMilliseconds, request.Vanity ?? "all", request.StartDate, request.EndDate);
            return stats.OrderByDescending(a => a.Date).ToList();
        }

        /// <summary>Streams only stored timestamps and returns daily click counts.</summary>
        /// <param name="request">The optional vanity, inclusive date bounds, and display time zone.</param>
        /// <returns>Chronologically ordered daily counts without individual telemetry.</returns>
        public virtual async Task<ClickDateList> GetDailyStats(UrlClickStatsRequest request)
        {
            var aggregation = new DailyClickAggregation(request);
            var columns = request.ExcludeCrawlers
                ? new[] { nameof(ClickStatsEntity.Datetime), nameof(ClickStatsEntity.IsCrawler) }
                : new[] { nameof(ClickStatsEntity.Datetime) };
            await foreach (var click in QueryClicks<TableEntity>(request, columns))
            {
                // Older rows have no IsCrawler column; filter here because a table query would drop them.
                if (request.ExcludeCrawlers && click.GetBoolean(nameof(ClickStatsEntity.IsCrawler)) == true)
                {
                    continue;
                }

                aggregation.Add(click.GetString(nameof(ClickStatsEntity.Datetime)));
            }

            return new ClickDateList { Items = aggregation.GetItems() };
        }

        public async Task<ShortUrlEntity> ArchiveShortUrlEntity(ShortUrlEntity urlEntity)
        {
            var originalUrl = await GetShortUrlEntity(urlEntity) ??
                              throw new InvalidOperationException("The link no longer exists.");
            originalUrl.IsArchived = true;

            return await SaveShortUrlEntity(originalUrl);
        }
    }
}