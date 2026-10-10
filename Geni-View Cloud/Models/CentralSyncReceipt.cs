using System;

namespace GeniView.Cloud.Models
{
    public sealed class CentralSyncReceipt
    {
        public long QueueId { get; set; }
        public Guid CommunityID { get; set; }
        public DateTimeOffset ProcessedAt { get; set; }
    }
}
