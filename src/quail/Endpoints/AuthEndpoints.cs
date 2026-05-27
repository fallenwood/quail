using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Quail.Services;

namespace Quail.Endpoints;

public static class AuthEndpoints
{
    public record RegisterRequest(string Username, string Email, string Password);
    public record LoginRequest(string Username, string Password);
    public record TokenResponse(string Token, string Username, string Email);

    public static RouteGroupBuilder MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/register", async (RegisterRequest req, AuthService auth) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new ErrorResponse("All fields are required"));

            if (req.Password.Length < 6)
                return Results.BadRequest(new ErrorResponse("Password must be at least 6 characters"));

            var user = await auth.RegisterAsync(req.Username, req.Email, req.Password);
            if (user is null)
                return Results.Conflict(new ErrorResponse("Username or email already exists"));

            return Results.Ok(new UserResponse(user.Id, user.Username, user.Email));
        }).RequireRateLimiting("auth");

        group.MapPost("/login", async (LoginRequest req, AuthService auth, HttpContext ctx) =>
        {
            var user = await auth.ValidateCredentialsAsync(req.Username, req.Password);
            if (user is null)
                return Results.Unauthorized();

            var principal = auth.GetClaimsPrincipal(user);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            return Results.Ok(new UserResponse(user.Id, user.Username, user.Email));
        }).RequireRateLimiting("auth");

        group.MapPost("/token", async (LoginRequest req, AuthService auth) =>
        {
            var user = await auth.ValidateCredentialsAsync(req.Username, req.Password);
            if (user is null)
                return Results.Unauthorized();

            var token = auth.GenerateJwtToken(user);
            return Results.Ok(new TokenResponse(token, user.Username, user.Email));
        }).RequireRateLimiting("auth");

        group.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        }).RequireAuthorization();

        group.MapGet("/me", (ClaimsPrincipal principal) =>
        {
            var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var username = principal.FindFirstValue(ClaimTypes.Name);
            var email = principal.FindFirstValue(ClaimTypes.Email);
            return Results.Ok(new MeResponse(id, username, email));
        }).RequireAuthorization();

        return group;
    }
}
