using Azure;
using Azure.Data.Tables;

namespace ShortenerTools.Core.Domain
{
    public class NextId : ITableEntity
    {
        public int Id { get; set; }
        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
    }
}