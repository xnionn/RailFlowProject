using System.Security.Claims;
using System.Security.Cryptography;
using RailFlow.Core.Functional;
using RailFlow.Infrastructure.Services;

namespace RailFlow.Infrastructure.Security;

/// <summary>
/// The signed-in user as the rest of the app sees it.
/// </summary>
public sealed record UserAccount(string UserName, string DisplayName, UserRole Role);

/// <summary>
/// One entry of the "DemoUsers" section in appsettings.json. Only a password hash is stored, never the password.
/// </summary>
public sealed class DemoUserEntry
{
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Passenger;
    public string PasswordHash { get; set; } = "";
}

/// <summary>
/// Role names used in cookies, [Authorize] attributes and the UI, plus the mapping to and from <see cref="UserRole"/>.
/// </summary>
public static class RoleNames
{
    public const string Passenger = "Passenger";
    public const string Dispatcher = "Dispatcher";

    public const string DisplayNameClaim = "railflow:display_name";

    public static string ToClaimValue(UserRole role) => role switch
    {
        UserRole.DispatcherAdmin => Dispatcher,
        _ => Passenger
    };

    /// <summary>
    /// Guests and users without a recognised role get the passenger view.
    /// </summary>
    public static UserRole FromPrincipal(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true && user.IsInRole(Dispatcher)
            ? UserRole.DispatcherAdmin
            : UserRole.Passenger;

    public static ClaimsPrincipal ToPrincipal(UserAccount account, string authenticationType) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, account.UserName),
                new Claim(DisplayNameClaim, account.DisplayName),
                new Claim(ClaimTypes.Role, ToClaimValue(account.Role))
            ],
            authenticationType));
}

/// <summary>
/// PBKDF2 (SHA-256) password hashing. Stored format: PBKDF2-SHA256.{iterations}.{salt base64}.{hash base64}.
/// </summary>
public static class PasswordHasher
{
    private const string Prefix = "PBKDF2-SHA256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    public const int DefaultIterations = 100_000;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}.{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// False for a wrong password and for a malformed stored hash; never throws on bad input.
    /// </summary>
    public static bool Verify(string password, string storedHash)
    {
        var parts = storedHash.Split('.');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out var iterations) || iterations <= 0)
            return false;

        var salt = TryDecode(parts[2]);
        var expected = TryDecode(parts[3]);
        if (salt is null || expected is null || expected.Length == 0)
            return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[]? TryDecode(string base64)
    {
        var buffer = new byte[base64.Length];
        return Convert.TryFromBase64String(base64, buffer, out var written) ? buffer[..written] : null;
    }
}

/// <summary>
/// Read-only list of demo accounts loaded from configuration. Authentication returns a Result instead of throwing,
/// and the failure message is the same for an unknown user and a wrong password.
/// </summary>
public sealed class DemoUserStore(IEnumerable<DemoUserEntry> entries)
{
    public const string InvalidCredentials = "Unknown user name or wrong password.";

    // Used when the user name is unknown, so both failure cases take about the same time.
    private static readonly string DummyHash = PasswordHasher.Hash("not-a-real-password");

    private readonly IReadOnlyDictionary<string, DemoUserEntry> _users = entries
        .Where(e => !string.IsNullOrWhiteSpace(e.UserName))
        .GroupBy(e => e.UserName.Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

    public int Count => _users.Count;

    public Result<UserAccount> Authenticate(string? userName, string? password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            return Result.Fail<UserAccount>("Enter a user name and a password.");

        if (!_users.TryGetValue(userName.Trim(), out var entry))
        {
            PasswordHasher.Verify(password, DummyHash);
            return Result.Fail<UserAccount>(InvalidCredentials);
        }

        return PasswordHasher.Verify(password, entry.PasswordHash)
            ? Result.Ok(new UserAccount(
                entry.UserName,
                string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.UserName : entry.DisplayName,
                entry.Role))
            : Result.Fail<UserAccount>(InvalidCredentials);
    }
}
