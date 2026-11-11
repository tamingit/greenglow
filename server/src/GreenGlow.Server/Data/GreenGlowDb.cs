using GreenGlow.Server.Cluster;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GreenGlow.Server.Data;

/// <summary>
/// The server's SQLite database: settings, users, connections, grants, logs, shares, macros and
/// sign-in keys, and the sync journal that copies them between nodes.
/// </summary>
public sealed class GreenGlowDb(DbContextOptions<GreenGlowDb> options) : DbContext(options)
{
    /// <summary>Set while applying rows from a peer: they keep the peer's versions and journal themselves.</summary>
    internal bool Applying { get; set; }

    /// <summary>Set by the sync recorder when a save journalled synced rows, so peers are poked after it.</summary>
    internal bool Journalled { get; set; }

    /// <summary>Set while a save of this context holds the sync write gate.</summary>
    internal bool HoldsWriteGate { get; set; }

    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<HostConnection> Connections => Set<HostConnection>();
    public DbSet<ConnectionGrant> Grants => Set<ConnectionGrant>();
    public DbSet<ConnectionLog> Logs => Set<ConnectionLog>();
    public DbSet<SessionShare> Shares => Set<SessionShare>();
    public DbSet<ShareRecipient> ShareRecipients => Set<ShareRecipient>();
    public DbSet<UserConnectionColor> UserColors => Set<UserConnectionColor>();
    public DbSet<Macro> Macros => Set<Macro>();
    public DbSet<MacroShare> MacroShares => Set<MacroShare>();
    public DbSet<MacroKey> MacroKeys => Set<MacroKey>();
    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();
    public DbSet<SyncVersion> SyncVersions => Set<SyncVersion>();
    public DbSet<SyncChange> SyncChanges => Set<SyncChange>();
    public DbSet<LogChange> LogChanges => Set<LogChange>();
    public DbSet<PeerCursor> PeerCursors => Set<PeerCursor>();
    public DbSet<LocalState> LocalState => Set<LocalState>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Setting>().HasKey(s => s.Key);

        model.Entity<AppUser>(user =>
        {
            user.HasIndex(u => u.Upn).IsUnique();
            user.Property(u => u.Upn).UseCollation("NOCASE");
        });

        model.Entity<HostConnection>(connection =>
        {
            connection.ToTable("Connections");
            connection.HasIndex(c => c.Name).IsUnique();
            connection.Ignore(c => c.Url);
            connection.HasMany(c => c.Grants).WithOne(g => g.Connection!).HasForeignKey(g => g.ConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<UserConnectionColor>(color =>
        {
            color.ToTable("UserColors");
            color.HasKey(c => new { c.UserId, c.ConnectionId });
            color.HasOne(c => c.Connection).WithMany().HasForeignKey(c => c.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ConnectionGrant>()
            .HasIndex(g => new { g.ConnectionId, g.PrincipalType, g.PrincipalId }).IsUnique();

        model.Entity<ConnectionLog>(log =>
        {
            log.Property(l => l.Kind).HasConversion<string>();
            log.HasIndex(l => l.StartedAt);
            log.HasIndex(l => l.SessionId);
            log.HasIndex(l => new { l.Node, l.EndedAt });
        });

        model.Entity<SessionShare>(share =>
        {
            share.HasIndex(s => s.Token).IsUnique();
            share.HasIndex(s => s.SessionId);
            share.HasMany(s => s.Recipients).WithOne(r => r.Share!).HasForeignKey(r => r.ShareId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<ShareRecipient>(recipient =>
        {
            recipient.Property(r => r.Access).HasConversion<string>();
            recipient.Property(r => r.Upn).UseCollation("NOCASE");
        });
        model.Entity<ConnectionGrant>().Property(g => g.PrincipalType).HasConversion<string>();

        model.Entity<Macro>(macro =>
        {
            macro.Property(m => m.Name).UseCollation("NOCASE");
            macro.HasIndex(m => new { m.OwnerUserId, m.Name }).IsUnique();
            macro.HasMany(m => m.Shares).WithOne(s => s.Macro!).HasForeignKey(s => s.MacroId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<MacroShare>(share =>
        {
            share.Property(s => s.PrincipalType).HasConversion<string>();
            share.HasIndex(s => new { s.MacroId, s.PrincipalType, s.PrincipalId }).IsUnique();
            share.HasIndex(s => s.PrincipalId);
        });
        model.Entity<MacroKey>(key =>
        {
            key.HasIndex(k => new { k.UserId, k.Keys }).IsUnique();
            key.HasOne(k => k.Macro).WithMany().HasForeignKey(k => k.MacroId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<SigningKey>().HasKey(k => k.Name);
        model.Entity<SyncVersion>().HasKey(v => new { v.Table, v.Key });
        model.Entity<SyncChange>(change =>
        {
            change.HasKey(c => c.Seq);
            change.HasIndex(c => c.At);
        });
        model.Entity<LogChange>(change =>
        {
            change.HasKey(c => c.Seq);
            change.HasIndex(c => c.At);
        });
        model.Entity<PeerCursor>().HasKey(c => c.Node);
        model.Entity<PeerCursor>().Property(c => c.Node).ValueGeneratedNever();
        model.Entity<LocalState>().HasKey(s => s.Key);

        // UUID keys are made in code (Guid.CreateVersion7), never by the database, so a new row
        // reached through a navigation (macro.Shares.Add) is inserted, not taken for an existing one.
        foreach (var entity in model.Model.GetEntityTypes())
            if (entity.FindPrimaryKey()?.Properties is [{ ClrType: var type } key] && type == typeof(Guid))
                model.Entity(entity.ClrType).Property(key.Name).ValueGeneratedNever();

        // SQLite can't order or compare DateTimeOffset values; store them as UTC ticks.
        foreach (var entity in model.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            if (property.ClrType == typeof(DateTimeOffset))
                property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
            else if (property.ClrType == typeof(DateTimeOffset?))
                property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
        }
    }
}

/// <summary>Lets `dotnet ef migrations add` build the model without starting the server.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<GreenGlowDb>
{
    public GreenGlowDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<GreenGlowDb>().UseSqlite("Data Source=design.db").Options);
}
