namespace HurrahTv.Shared.Models;

// user-submitted feedback (#19). Category is validated server-side against the allowed set.
// the Website field is a honeypot — hidden in the UI, so a non-empty value means a bot and the
// submission is silently dropped (the server still returns success so bots can't tell).
public class FeedbackSubmission
{
    public string Category { get; set; } = "general"; // "bug" | "feature" | "general"
    public string Message { get; set; } = "";
    public string? ContactEmail { get; set; }
    public string? Website { get; set; } // honeypot — real users leave this empty
}
