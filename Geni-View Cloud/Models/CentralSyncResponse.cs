using System;
using System.Collections.Generic;

namespace GeniView.Cloud.Models
{
    public sealed class CentralSyncResponse
    {
        public Guid CommunityId { get; init; }
        public int Received { get; init; }
        public int Processed { get; init; }
        public int AlreadyProcessed { get; init; }
        public List<long> ProcessedQueueIds { get; init; } = new();
    }
}
