using System;
using System.Collections.Generic;

namespace GeniView.Cloud.Models
{
    public sealed class CentralSyncBatch
    {
        public Guid CommunityId { get; init; }
        public List<CentralSyncBatchMessage> Messages { get; init; } = new();
    }

    public sealed class CentralSyncBatchMessage
    {
        public long QueueId { get; init; }
        public string Topic { get; init; }
        public string Payload { get; init; }
    }
}
