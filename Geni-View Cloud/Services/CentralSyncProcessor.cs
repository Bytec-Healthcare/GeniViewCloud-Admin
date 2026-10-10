using GeniView.Cloud.Models;
using GeniView.Cloud.Repository;
using GeniView.Data.Hardware;
using GeniView.Data.Hardware.Event;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static GeniView.Cloud.Common.DataDefine;

namespace GeniView.Cloud.Services
{
    /// <summary>
    /// Processes data received from a hospital Sync Worker.
    /// It reuses the existing G3BatteryDataRepository transformation logic,
    /// while supplying the Community from the sync request rather than MQTT state.
    /// </summary>
    public sealed class CentralSyncProcessor
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly GeniViewCloudDataRepository _db;
        private readonly G3BatteryDataRepository _g3Repository;

        public CentralSyncProcessor(
            GeniViewCloudDataRepository db,
            G3BatteryDataRepository g3Repository)
        {
            _db = db;
            _g3Repository = g3Repository;
        }

        public async Task<CentralSyncResponse> ProcessBatchAsync(
            CentralSyncBatch batch,
            CancellationToken cancellationToken = default)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (batch.Messages == null || batch.Messages.Count == 0)
            {
                return new CentralSyncResponse
                {
                    CommunityId = batch.CommunityId,
                    Received = 0,
                    Processed = 0,
                    AlreadyProcessed = 0,
                    ProcessedQueueIds = new List<long>()
                };
            }

            var community = await _db.Communities
                .SingleOrDefaultAsync(x => x.CommunityID == batch.CommunityId, cancellationToken);

            if (community == null)
                throw new InvalidOperationException($"CommunityID '{batch.CommunityId}' was not found in the central database.");

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var processed = new List<long>();
            var alreadyProcessed = new List<long>();

            foreach (var message in batch.Messages.OrderBy(x => x.QueueId))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.Equals(message.Topic?.Split('/').FirstOrDefault(), "battery", StringComparison.OrdinalIgnoreCase) ||
                    !message.Topic.StartsWith("battery/log/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Unsupported sync topic '{message.Topic}'. Flow 1 currently supports battery/log only.");
                }

                if (await IsAlreadyProcessedAsync(message.QueueId, cancellationToken))
                {
                    alreadyProcessed.Add(message.QueueId);
                    continue;
                }

                await ProcessBatteryLogAsync(message, community.ID, cancellationToken);
                await AddReceiptAsync(message.QueueId, batch.CommunityId, cancellationToken);
                processed.Add(message.QueueId);
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new CentralSyncResponse
            {
                CommunityId = batch.CommunityId,
                Received = batch.Messages.Count,
                Processed = processed.Count,
                AlreadyProcessed = alreadyProcessed.Count,
                ProcessedQueueIds = processed.Concat(alreadyProcessed).ToList()
            };
        }

        private async Task ProcessBatteryLogAsync(
            CentralSyncBatchMessage message,
            long communityDbId,
            CancellationToken cancellationToken)
        {
            var batteryId = message.Topic.Substring("battery/log/".Length);
            if (string.IsNullOrWhiteSpace(batteryId))
                throw new InvalidOperationException($"Battery ID is missing from topic '{message.Topic}'.");

            List<G3BatteryDockLog> logs;
            try
            {
                logs = JsonConvert.DeserializeObject<List<G3BatteryDockLog>>(message.Payload);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Invalid battery/log payload for topic '{message.Topic}'.", ex);
            }

            if (logs == null || logs.Count == 0)
                throw new InvalidOperationException($"Battery/log payload for '{message.Topic}' contains no records.");

            var transfer = G3BatteryDataRepository.transfer(batteryId, logs);
            var device = transfer.device;
            var battery = transfer.battery;
            var deviceEvents = transfer.deviceEvents ?? new List<DeviceEvent>();

            device.CommunityID = communityDbId;
            device.Community = null;
            battery.CommunityID = communityDbId;
            battery.Community = null;

            // The existing application treats Battery.SerialNumberCode and Device.SerialNumber as unique.
            // Reuse the existing rows when they already exist; create them otherwise.
            var existingDevice = await _db.Devices
                .SingleOrDefaultAsync(x => x.SerialNumber == device.SerialNumber, cancellationToken);
            var existingBattery = await _db.Batteries
                .SingleOrDefaultAsync(x => x.SerialNumberCode == battery.SerialNumberCode, cancellationToken);

            if (existingDevice == null)
            {
                device.ClearNavigationProperties();
                _db.Devices.Add(device);
                await _db.SaveChangesAsync(cancellationToken);
                existingDevice = device;
            }
            else
            {
                existingDevice.CommunityID = communityDbId;
                existingDevice.FirmwareVersion = device.FirmwareVersion;
                existingDevice.Manufacturer = device.Manufacturer;
                existingDevice.ProductName = device.ProductName;
                existingDevice.DeviceType = device.DeviceType;
            }

            if (existingBattery == null)
            {
                battery.ClearNavigationProperties();
                _db.Batteries.Add(battery);
                await _db.SaveChangesAsync(cancellationToken);
                existingBattery = battery;
            }
            else
            {
                existingBattery.CommunityID = communityDbId;
                existingBattery.FirmwareVersion = battery.FirmwareVersion;
                existingBattery.BatteryConfiguration = battery.BatteryConfiguration;
                existingBattery.BatteryPackFirmwareVersion = battery.BatteryPackFirmwareVersion;
                existingBattery.BatteryChemistry = battery.BatteryChemistry;
                existingBattery.BatteryTechnology = battery.BatteryTechnology;
            }

            var agentId = await _db.Agents
                .Where(x => x.Name.ToLower() == "default")
                .Select(x => (long?)x.ID)
                .FirstOrDefaultAsync(cancellationToken) ?? -1;

            var internalBatteryLogs = battery.InternalBatteryLogCollection?.ToList() ?? new List<InternalBatteryLog>();
            foreach (var log in internalBatteryLogs)
            {
                log.Battery_ID = existingBattery.ID;
                log.Agent_ID = agentId;
            }

            var agentBatteryLogs = battery.AgentBatteryLogCollection?.ToList() ?? new List<AgentBatteryLog>();
            foreach (var log in agentBatteryLogs)
            {
                log.Battery_ID = existingBattery.ID;
                log.Agent_ID = agentId;
            }

            var internalDeviceLogs = device.InternalDeviceLogCollection?.ToList() ?? new List<InternalDeviceLog>();
            foreach (var log in internalDeviceLogs)
            {
                log.Device_ID = existingDevice.ID;
                log.Agent_ID = agentId;
            }

            var agentDeviceLogs = device.AgentDeviceLogCollection?.ToList() ?? new List<AgentDeviceLog>();
            foreach (var log in agentDeviceLogs)
            {
                log.Device_ID = existingDevice.ID;
                log.Agent_ID = agentId;
            }

            foreach (var deviceEvent in deviceEvents)
            {
                deviceEvent.Device_ID = existingDevice.ID;
            }

            // Do not add navigation collections to the Battery/Device entities themselves;
            // add the child rows explicitly, matching the existing LogApiController flow.
            if (internalBatteryLogs.Count > 0) _db.InternalBatteryLog.AddRange(internalBatteryLogs);
            if (agentBatteryLogs.Count > 0) _db.AgentBatteryLog.AddRange(agentBatteryLogs);
            if (internalDeviceLogs.Count > 0) _db.InternalDeviceLog.AddRange(internalDeviceLogs);
            if (agentDeviceLogs.Count > 0) _db.AgentDeviceLog.AddRange(agentDeviceLogs);
            if (deviceEvents.Count > 0) _db.DeviceEvents.AddRange(deviceEvents);

            _logger.Info(
                "Central Sync processed battery/log. QueueId={QueueId}, CommunityID={CommunityId}, Battery={Battery}, Device={Device}, BatteryLogs={BatteryLogs}, DeviceLogs={DeviceLogs}, Events={Events}",
                message.QueueId,
                communityDbId,
                battery.SerialNumber,
                device.SerialNumber,
                internalBatteryLogs.Count + agentBatteryLogs.Count,
                internalDeviceLogs.Count + agentDeviceLogs.Count,
                deviceEvents.Count);
        }

        private Task<bool> IsAlreadyProcessedAsync(long queueId, CancellationToken cancellationToken)
        {
            return _db.CentralSyncReceipts.AnyAsync(x => x.QueueId == queueId, cancellationToken);
        }

        private Task AddReceiptAsync(long queueId, Guid communityId, CancellationToken cancellationToken)
        {
            _db.CentralSyncReceipts.Add(new CentralSyncReceipt
            {
                QueueId = queueId,
                CommunityID = communityId,
                ProcessedAt = DateTimeOffset.UtcNow
            });

            return Task.CompletedTask;
        }
    }
}
