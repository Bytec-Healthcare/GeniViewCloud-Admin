using GeniView.Cloud.Models;
using GeniView.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using NLog;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace GeniView.Cloud.Controllers.API
{
    /// <summary>
    /// Receives batched MQTT payloads from hospital Sync Workers over the Cloudflare Mesh.
    /// Flow 1 currently accepts battery/log/# messages only.
    /// </summary>
    [ApiController]
    [Route("api/central-sync")]
    public sealed class CentralSyncController : ControllerBase
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly CentralSyncProcessor _processor;

        public CentralSyncController(CentralSyncProcessor processor)
        {
            _processor = processor;
        }

        [HttpPost("messages")]
        public async Task<IActionResult> Messages(
            [FromBody] CentralSyncBatch batch,
            CancellationToken cancellationToken)
        {
            if (batch == null)
                return BadRequest(new { message = "Request body is required." });

            if (batch.CommunityId == Guid.Empty)
                return BadRequest(new { message = "CommunityId is required." });

            if (batch.Messages == null || batch.Messages.Count == 0)
                return BadRequest(new { message = "At least one message is required." });

            if (batch.Messages.Count > 500)
                return BadRequest(new { message = "Maximum batch size is 500 messages." });

            if (batch.Messages.Any(x => x.QueueId <= 0 || string.IsNullOrWhiteSpace(x.Topic) || string.IsNullOrWhiteSpace(x.Payload)))
                return BadRequest(new { message = "Every message requires QueueId, Topic and Payload." });

            if (batch.Messages.Select(x => x.QueueId).Distinct().Count() != batch.Messages.Count)
                return BadRequest(new { message = "QueueId values must be unique within a batch." });

            try
            {
                var result = await _processor.ProcessBatchAsync(batch, cancellationToken);

                _logger.Info(
                    "Central Sync API accepted {Received} messages for CommunityID={CommunityId}. Processed={Processed}, AlreadyProcessed={AlreadyProcessed}.",
                    result.Received,
                    batch.CommunityId,
                    result.Processed,
                    result.AlreadyProcessed);

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                _logger.Warn(ex, "Central Sync validation/processing failure for CommunityID={CommunityId}.", batch.CommunityId);
                return StatusCode((int)HttpStatusCode.UnprocessableEntity, new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Central Sync API failed for CommunityID={CommunityId}.", batch.CommunityId);
                return StatusCode((int)HttpStatusCode.InternalServerError, new { message = "Central sync processing failed." });
            }
        }
    }
}
