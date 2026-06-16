using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenTelemetry;

namespace HurrahTv.Api.Telemetry;

// strips PII from spans before they reach App Insights (#24/#200). Auth traffic carries
// phone numbers and OTP codes, but OpenTelemetry's ASP.NET Core instrumentation records
// neither request bodies nor headers — only URL parts, status, and duration. The realistic
// vector is a phone/code that slips into the query string (the url.query tag), with the
// legacy http.url/http.target tags as fallbacks; redact those defensively.
//
// This replaces the 2.x ITelemetryInitializer, which App Insights SDK 3.x removed when it
// moved onto OpenTelemetry. Registered via ConfigureOpenTelemetryTracerProvider in Program.cs.
public sealed partial class PiiRedactionProcessor : BaseProcessor<Activity>
{
    private static readonly string[] UrlBearingTags = ["url.query", "url.full", "url.path", "http.url", "http.target"];

    public override void OnEnd(Activity activity)
    {
        foreach (string tag in UrlBearingTags)
        {
            if (activity.GetTagItem(tag) is string value && value.Length > 0)
            {
                string redacted = Redact(value);
                if (redacted != value)
                    activity.SetTag(tag, redacted);
            }
        }
    }

    // public for direct unit testing of the redaction rules
    public static string Redact(string value)
    {
        // redact values of sensitive query keys first (tokens/jwts aren't numeric so
        // the digit-run pass below wouldn't catch them)
        string result = SensitiveQueryParam().Replace(value, "$1=[redacted]");
        // then collapse phone-number-length digit runs anywhere. 10 is the E.164 phone
        // length; staying at 10+ leaves shorter TMDb ids (e.g. /details/tv/1396) intact
        return LongDigitRun().Replace(result, "[redacted]");
    }

    [GeneratedRegex(@"(phone|phonenumber|code|otp|token|jwt|key)=[^&]*", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveQueryParam();

    [GeneratedRegex(@"\d{10,}")]
    private static partial Regex LongDigitRun();
}
