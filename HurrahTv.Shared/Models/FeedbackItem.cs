namespace HurrahTv.Shared.Models;

// one feedback row for the admin view (#19). Phone is joined from Users so an admin can see who
// submitted; ContactEmail is the optional reply address the user chose to provide.
public record FeedbackItem(
    int Id,
    string UserId,
    string? Phone,
    string Category,
    string Message,
    string? ContactEmail,
    DateTime CreatedAt);
