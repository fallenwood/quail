using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Quail.Data;
using Quail.Models;

namespace Quail.Services;

public class AuthService(QuailDbContext db, IConfiguration config)
{
    public async Task<User?> RegisterAsync(string username, string email, string password)
    {
        if (await db.Users.AnyAsync(u => u.Username == username || u.Email == email))
            return null;

        var user = new User
        {
            Username = username,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Create default mailboxes
        var mailboxes = new[]
        {
            new Mailbox { UserId = user.Id, Name = "INBOX", SpecialUse = SpecialFolder.Inbox },
            new Mailbox { UserId = user.Id, Name = "Sent", SpecialUse = SpecialFolder.Sent },
            new Mailbox { UserId = user.Id, Name = "Drafts", SpecialUse = SpecialFolder.Drafts },
            new Mailbox { UserId = user.Id, Name = "Trash", SpecialUse = SpecialFolder.Trash },
            new Mailbox { UserId = user.Id, Name = "Junk", SpecialUse = SpecialFolder.Junk },
            new Mailbox { UserId = user.Id, Name = "Outbox", SpecialUse = SpecialFolder.Outbox },
        };

        db.Mailboxes.AddRange(mailboxes);
        await db.SaveChangesAsync();

        return user;
    }

    // Dummy hash used to normalize timing when user doesn't exist (prevents user enumeration)
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dummy");

    public async Task<User?> ValidateCredentialsAsync(string usernameOrEmail, string password)
    {
        var login = usernameOrEmail.Trim();
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == login || u.Email == login);

        // Always verify against a hash to prevent timing-based user enumeration
        var hashToVerify = user?.PasswordHash ?? DummyHash;
        var isValid = BCrypt.Net.BCrypt.Verify(password, hashToVerify);

        if (user is null || !isValid)
        {
            return null;
        }

        return user;
    }

    public string GenerateJwtToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            config["Jwt:Key"] ?? throw new InvalidOperationException("JWT key not configured")));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Email, user.Email)
        };

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public ClaimsPrincipal GetClaimsPrincipal(User user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Email, user.Email)
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "cookie"));
    }
}
