using Microsoft.AspNetCore.Identity;
using RemoteCI.Shared;

namespace RemoteCI.Server.Data;

public sealed class AppUser : IdentityUser<Guid>, UserProfileLike
{
    public string DisplayName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.User;
    public Guid RoleDefinitionId { get; set; } = AccountRole.StudentId;
    public AccountRole RoleDefinition { get; set; } = null!;
    public UserPermissions GrantedPermissions { get; set; }
    public bool Enabled { get; set; } = true;
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
