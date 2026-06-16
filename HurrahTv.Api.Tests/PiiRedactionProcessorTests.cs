using System.Diagnostics;
using HurrahTv.Api.Telemetry;

namespace HurrahTv.Api.Tests;

// pins the App Insights PII scrubber (#24/#200): phone numbers and tokens that leak into a
// span's URL tags must never reach telemetry, while diagnostic ids stay readable. Exercises
// the OpenTelemetry BaseProcessor<Activity> directly — a plain Activity needs no listener for
// SetTag/GetTagItem.
public class PiiRedactionProcessorTests
{
    private static string? TagAfterProcessing(string tag, string value)
    {
        Activity activity = new("request");
        activity.SetTag(tag, value);
        new PiiRedactionProcessor().OnEnd(activity);
        return activity.GetTagItem(tag) as string;
    }

    [Fact]
    public void Redacts_Phone_In_Query_Tag()
    {
        string? result = TagAfterProcessing("url.query", "phone=15551234567&service=8");
        Assert.DoesNotContain("15551234567", result);
        Assert.Contains("[redacted]", result);
        // a non-sensitive param survives so the span stays useful for diagnosis
        Assert.Contains("service=8", result);
    }

    [Fact]
    public void Redacts_Token_Query_Param_Even_When_Non_Numeric()
    {
        string? result = TagAfterProcessing("url.query", "token=abc.def.ghi");
        Assert.DoesNotContain("abc.def.ghi", result);
    }

    [Fact]
    public void Does_Not_Redact_Sensitive_Word_As_A_Substring_Of_Another_Param()
    {
        // "monkey" ends in "key" but is not a sensitive param — must survive intact
        string? result = TagAfterProcessing("url.query", "monkey=banana&token=secret");
        Assert.Contains("monkey=banana", result);
        Assert.DoesNotContain("secret", result);
    }

    [Fact]
    public void Redacts_Phone_Length_Digit_Run_In_Path_Tag()
    {
        string? result = TagAfterProcessing("url.path", "/x/15551234567/y");
        Assert.DoesNotContain("15551234567", result);
    }

    [Fact]
    public void Preserves_Short_Tmdb_Id_In_Path_Tag()
    {
        // a 4-7 digit TMDb id is below the 10-digit phone threshold and must stay readable
        string? result = TagAfterProcessing("url.path", "/details/tv/1396");
        Assert.Equal("/details/tv/1396", result);
    }

    [Fact]
    public void Leaves_Non_Url_Tags_Untouched()
    {
        Activity activity = new("request");
        activity.SetTag("http.response.status_code", "200");
        new PiiRedactionProcessor().OnEnd(activity);
        Assert.Equal("200", activity.GetTagItem("http.response.status_code") as string);
    }

    [Fact]
    public void Redact_Helper_Is_Idempotent_On_Clean_Input() =>
        Assert.Equal("/api/health", PiiRedactionProcessor.Redact("/api/health"));
}
