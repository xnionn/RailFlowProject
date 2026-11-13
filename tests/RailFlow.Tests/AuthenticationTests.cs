using System.Security.Claims;
using RailFlow.Core.Functional;
using RailFlow.Infrastructure.Security;
using RailFlow.Infrastructure.Services;

namespace RailFlow.Tests;

public sealed class AuthenticationTests
{
    // Few iterations keep the tests fast; production hashes use PasswordHasher.DefaultIterations.
    private static DemoUserEntry Entry(string user, string password, UserRole role, string display = "") => new()
    {
        UserName = user,
        DisplayName = display,
        Role = role,
        PasswordHash = PasswordHasher.Hash(password, iterations: 1_000)
    };

    private static DemoUserStore Store() => new(
    [
        Entry("passenger", "pass-1", UserRole.Passenger, "Demo Passenger"),
        Entry("dispatcher", "disp-1", UserRole.Dispatcher, "Demo Dispatcher")
    ]);

    [Fact]
    public void Hash_ThenVerify_AcceptsOnlyTheOriginalPassword()
    {
        var hash = PasswordHasher.Hash("correct horse", iterations: 1_000);

        Assert.True(PasswordHasher.Verify("correct horse", hash));
        Assert.False(PasswordHasher.Verify("correct horsE", hash));
        Assert.False(PasswordHasher.Verify("", hash));
    }

    [Fact]
    public void Hash_UsesRandomSalt_SoSamePasswordGivesDifferentHashes()
    {
        var first = PasswordHasher.Hash("same", iterations: 1_000);
        var second = PasswordHasher.Hash("same", iterations: 1_000);

        Assert.NotEqual(first, second);
        Assert.StartsWith("PBKDF2-SHA256.1000.", first);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain-text-password")]
    [InlineData("PBKDF2-SHA256.notanumber.AAAA.AAAA")]
    [InlineData("PBKDF2-SHA256.1000.@@@.AAAA")]
    [InlineData("MD5.1000.AAAA.AAAA")]
    public void Verify_ReturnsFalse_ForMalformedStoredHash(string storedHash)
    {
        Assert.False(PasswordHasher.Verify("anything", storedHash));
    }

    [Fact]
    public void Authenticate_WithCorrectPassword_ReturnsAccountWithRole()
    {
        var result = Store().Authenticate("Dispatcher", "disp-1");

        var account = Assert.IsType<Success<UserAccount>>(result).Data;
        Assert.Equal("dispatcher", account.UserName);
        Assert.Equal("Demo Dispatcher", account.DisplayName);
        Assert.Equal(UserRole.Dispatcher, account.Role);
    }

    [Fact]
    public void Authenticate_GivesSameMessage_ForWrongPasswordAndUnknownUser()
    {
        var store = Store();

        var wrongPassword = Assert.IsType<Failure<UserAccount>>(store.Authenticate("passenger", "disp-1"));
        var unknownUser = Assert.IsType<Failure<UserAccount>>(store.Authenticate("nobody", "pass-1"));

        Assert.Equal(DemoUserStore.InvalidCredentials, wrongPassword.Reason);
        Assert.Equal(wrongPassword.Reason, unknownUser.Reason);
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("passenger", null)]
    [InlineData("   ", "x")]
    public void Authenticate_WithMissingInput_FailsWithoutThrowing(string? user, string? password)
    {
        Assert.IsType<Failure<UserAccount>>(Store().Authenticate(user, password));
    }

    [Fact]
    public void Authenticate_FallsBackToUserName_WhenDisplayNameIsEmpty()
    {
        var store = new DemoUserStore([Entry("anna", "pw", UserRole.Passenger)]);

        var account = Assert.IsType<Success<UserAccount>>(store.Authenticate("anna", "pw")).Data;

        Assert.Equal("anna", account.DisplayName);
    }

    [Fact]
    public void Principal_RoundTripsRole()
    {
        var dispatcher = RoleNames.ToPrincipal(new UserAccount("d", "D", UserRole.Dispatcher), "test");
        var passenger = RoleNames.ToPrincipal(new UserAccount("p", "P", UserRole.Passenger), "test");

        Assert.True(dispatcher.IsInRole(RoleNames.Dispatcher));
        Assert.Equal(UserRole.Dispatcher, RoleNames.FromPrincipal(dispatcher));
        Assert.Equal(UserRole.Passenger, RoleNames.FromPrincipal(passenger));
        Assert.Equal("D", dispatcher.FindFirst(RoleNames.DisplayNameClaim)?.Value);
    }

    [Fact]
    public void FromPrincipal_TreatsGuestsAsPassengers_EvenWithRoleClaim()
    {
        // A role claim on an unauthenticated identity must not grant dispatcher rights.
        var forged = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, RoleNames.Dispatcher)]));

        Assert.Equal(UserRole.Passenger, RoleNames.FromPrincipal(forged));
        Assert.Equal(UserRole.Passenger, RoleNames.FromPrincipal(null));
    }
}
