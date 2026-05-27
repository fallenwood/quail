using System.Text.Json.Serialization;
using Quail.Endpoints;

namespace Quail;

// Shared response/request DTOs used across endpoints
public record ErrorResponse(string Error);
public record UserResponse(int Id, string Username, string Email);
public record MeResponse(string? Id, string? Username, string? Email);
public record MessageResponse(string Message);
public record EmailListResponse(int Total, int Page, int PageSize, List<EmailEndpoints.EmailSummary> Messages);

[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(UserResponse))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(EmailListResponse))]
[JsonSerializable(typeof(AuthEndpoints.RegisterRequest))]
[JsonSerializable(typeof(AuthEndpoints.LoginRequest))]
[JsonSerializable(typeof(AuthEndpoints.TokenResponse))]
[JsonSerializable(typeof(EmailEndpoints.EmailSummary))]
[JsonSerializable(typeof(EmailEndpoints.EmailDetail))]
[JsonSerializable(typeof(EmailEndpoints.ComposeRequest))]
[JsonSerializable(typeof(EmailEndpoints.MoveRequest))]
[JsonSerializable(typeof(MailboxEndpoints.MailboxInfo))]
[JsonSerializable(typeof(MailboxEndpoints.MailboxInfo[]))]
[JsonSerializable(typeof(MailboxEndpoints.CreateMailboxRequest))]
[JsonSerializable(typeof(List<EmailEndpoints.EmailSummary>))]
[JsonSerializable(typeof(List<MailboxEndpoints.MailboxInfo>))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
