
using Chat.Server.Auth;

namespace Tests;

public class PasswordHasherTests
{
    [Fact]
    public void HashAndVerify_CorrectPassword_Succeeds()
    {
        var hasher = new PasswordHasher();

        var result = hasher.HashPassword("CorrectHorseBattery12");

        Assert.True(hasher.VerifyPassword(
            "CorrectHorseBattery12",
            result.Salt,
            result.Hash,
            result.Iterations));
    }

    [Fact]
    public void Verify_WrongPassword_Fails()
    {
        var hasher = new PasswordHasher();

        var result = hasher.HashPassword("CorrectHorseBattery12");

        Assert.False(hasher.VerifyPassword(
            "WrongPassword123",
            result.Salt,
            result.Hash,
            result.Iterations));
    }

    [Fact]
    public void HashPassword_UsesDifferentSalt()
    {
        var hasher = new PasswordHasher();

        var first = hasher.HashPassword("SamePassword123");
        var second = hasher.HashPassword("SamePassword123");

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
    }
}
