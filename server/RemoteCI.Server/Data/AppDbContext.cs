using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace RemoteCI.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<DeviceSession> DeviceSessions => Set<DeviceSession>();
    public DbSet<PluginCredential> PluginCredentials => Set<PluginCredential>();
    public DbSet<PluginPairingCode> PluginPairingCodes => Set<PluginPairingCode>();
    public DbSet<SystemMetadata> SystemMetadata => Set<SystemMetadata>();
    public DbSet<AccountRole> AccountRoles => Set<AccountRole>();
    public DbSet<BackupConfiguration> BackupConfigurations => Set<BackupConfiguration>();
    public DbSet<UserCardLayout> UserCardLayouts => Set<UserCardLayout>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>(entity =>
        {
            entity.Property(x => x.DisplayName).HasMaxLength(40);
            entity.HasIndex(x => x.Version);
            entity.HasOne(x => x.RoleDefinition).WithMany(x => x.Users).HasForeignKey(x => x.RoleDefinitionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<AccountRole>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(40);
            entity.Property(x => x.NormalizedName).HasMaxLength(40);
            entity.HasIndex(x => x.NormalizedName).IsUnique();
        });
        builder.Entity<BackupConfiguration>(entity =>
        {
            entity.Property(x => x.LastError).HasMaxLength(1000);
        });
        builder.Entity<UserCardLayout>(entity =>
        {
            entity.HasKey(x => new { x.UserId, x.PageKey });
            entity.Property(x => x.PageKey).HasMaxLength(64);
            entity.Property(x => x.LayoutJson).HasMaxLength(32_768);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<DeviceSession>(entity =>
        {
            entity.HasIndex(x => x.AccessTokenHash).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.RevokedAt });
            entity.Property(x => x.DeviceName).HasMaxLength(80);
            entity.Property(x => x.VerifierHash).HasMaxLength(64);
            entity.Property(x => x.AccessTokenHash).HasMaxLength(64);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<PluginCredential>(entity =>
        {
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.Property(x => x.TokenHash).HasMaxLength(64);
            entity.Property(x => x.Name).HasMaxLength(80);
        });
        builder.Entity<PluginPairingCode>(entity =>
        {
            entity.HasIndex(x => x.CodeHash).IsUnique();
            entity.Property(x => x.CodeHash).HasMaxLength(64);
        });
    }
}
