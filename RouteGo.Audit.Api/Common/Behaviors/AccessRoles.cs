namespace RouteGo.Audit.Api.Common.Behaviors;

/// <summary>The role values Identity writes into the role claim, as endpoints name them in [Authorize].</summary>
public static class AccessRoles
{
    /// <summary>RouteGo staff; the only role that may read the audit log.</summary>
    public const string Admin = "Admin";
}
