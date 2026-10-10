using GeniView.Cloud.Common;
using GeniView.Cloud.Models;
using GeniView.Data.Agent;
using System;
using GeniView.Data.Hardware;
using GeniView.Data.Hardware.Event;
using GeniView.Data.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Linq;

namespace GeniView.Cloud.Repository
{
    public partial class GeniViewCloudDataRepository : DbContext
    {
        public GeniViewCloudDataRepository(DbContextOptions<GeniViewCloudDataRepository> options)
            : base(options)
        {
        }

        // Parameterless constructor for use in repositories until DI is fully wired in Phase 3.
        // TODO Phase 3: remove this and inject via IServiceProvider in all callers.
        public GeniViewCloudDataRepository()
            : base(new DbContextOptionsBuilder<GeniViewCloudDataRepository>().Options)
        {
        }

        public virtual DbSet<Agent> Agents { get; set; }
        public virtual DbSet<Battery> Batteries { get; set; }
        public virtual DbSet<AgentBatteryLog> AgentBatteryLog { get; set; }
        public virtual DbSet<InternalBatteryLog> InternalBatteryLog { get; set; }
        public virtual DbSet<InternalDeviceLog> InternalDeviceLog { get; set; }
        public virtual DbSet<Community> Communities { get; set; }
        public virtual DbSet<AgentDeviceLog> AgentDeviceLog { get; set; }
        public virtual DbSet<Device> Devices { get; set; }
        public virtual DbSet<DeviceEvent> DeviceEvents { get; set; }
        public virtual DbSet<Group> Groups { get; set; }
        public virtual DbSet<CentralSyncReceipt> CentralSyncReceipts { get; set; }

        public virtual DbSet<MailServer> MailServer { get; set; }
        public virtual DbSet<DeviceEventNotification> DeviceEventActionNotifications { get; set; }
        public virtual DbSet<ApplicationUpdate> ApplicationUpdates { get; set; }
        public virtual DbSet<ApplicationLog> ApplicationLogs { get; set; }
        public virtual DbSet<UserActivityHistory> UserActivityHistory { get; set; }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            // With <Nullable>disable</Nullable>, EF Core 8 treats every reference-type
            // [ComplexType] property as optional, which it does not support at any nesting
            // depth. This convention walks the whole type hierarchy and marks them all required.
            configurationBuilder.Conventions.Add(_ => new RequiredComplexPropertiesConvention());
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                // AppContext.BaseDirectory always resolves to the app's deployment folder.
                // Directory.GetCurrentDirectory() returns C:\Windows\System32 under IIS on .NET 8,
                // which caused appsettings.json to not be found and the connection string to be null.
                var basePath = !string.IsNullOrEmpty(Global._serverPath)
                    ? Global._serverPath
                    : AppContext.BaseDirectory;

                var config = new ConfigurationBuilder()
                    .SetBasePath(basePath)
                    .AddJsonFile("appsettings.json")
                    .Build();
                optionsBuilder.UseNpgsql(
                    config.GetConnectionString("GeniViewCloudDataRepository"),
                    npgsql => npgsql.CommandTimeout(300)); // 5 min — chart queries scan 2-hour log tables
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<CentralSyncReceipt>(entity =>
            {
                entity.ToTable("CentralSyncReceipts");
                entity.HasKey(x => x.QueueId);
                entity.Property(x => x.CommunityID).IsRequired();
                entity.Property(x => x.ProcessedAt).IsRequired();
            });

            // EF6 used PluralizingTableNameConvention (entity class name → plural table name).
            // EF Core uses the DbSet property name as-is. Map each mismatched table explicitly.
            modelBuilder.Entity<AgentDeviceLog>().ToTable("AgentDeviceLogs");
            modelBuilder.Entity<AgentBatteryLog>().ToTable("AgentBatteryLogs");
            modelBuilder.Entity<InternalBatteryLog>().ToTable("InternalBatteryLogs");
            modelBuilder.Entity<InternalDeviceLog>().ToTable("InternalDeviceLogs");
            modelBuilder.Entity<MailServer>().ToTable("MailServers");
            modelBuilder.Entity<DeviceEventNotification>().ToTable("DeviceEventNotifications");
            modelBuilder.Entity<UserActivityHistory>().ToTable("UserActivityHistories");

            // EF6 convention for implicit FK columns: {NavigationPropertyName}_{PKPropertyName}
            // EF Core convention for shadow FKs:     {NavigationPropertyName}{PKPropertyName}
            // Configure column names explicitly for entities that couldn't have [Column] attributes added.
            modelBuilder.Entity<InternalDeviceLog>()
                .HasOne<Device>(idl => idl.Device)
                .WithMany(d => d.InternalDeviceLogCollection)
                .HasForeignKey("Device_ID");

            modelBuilder.Entity<InternalBatteryLog>()
                .HasOne<Battery>(ibl => ibl.Battery)
                .WithMany(b => b.InternalBatteryLogCollection)
                .HasForeignKey("Battery_ID");
        }

        // Marks every [ComplexType] property as required at any nesting depth.
        // RequiredComplexPropertiesConvention handles the case where <Nullable>disable</Nullable>
        // causes EF Core 8 to treat all reference-type complex properties as optional (unsupported).
        private sealed class RequiredComplexPropertiesConvention : IModelFinalizingConvention
        {
            public void ProcessModelFinalizing(
                IConventionModelBuilder modelBuilder,
                IConventionContext<IConventionModelBuilder> context)
            {
                foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
                {
                    MakeComplexPropertiesRequired(entityType);
                }
            }

            private static void MakeComplexPropertiesRequired(IConventionTypeBase typeBase)
            {
                foreach (var cp in typeBase.GetMembers().OfType<IConventionComplexProperty>())
                {
                    cp.Builder.IsRequired(true);
                    MakeComplexPropertiesRequired(cp.ComplexType);
                }
            }
        }
    }
}
