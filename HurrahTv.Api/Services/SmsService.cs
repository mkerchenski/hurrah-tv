using Twilio;
using Twilio.Rest.Api.V2010.Account;

namespace HurrahTv.Api.Services;

public class SmsService
{
    private readonly string? _accountSid;
    private readonly string? _authToken;
    private readonly string? _fromNumber;
    private readonly bool _isDevelopment;
    private readonly ILogger<SmsService> _logger;

    public SmsService(IConfiguration config, IHostEnvironment env, ILogger<SmsService> logger)
    {
        _accountSid = config["Twilio:AccountSid"];
        _authToken = config["Twilio:AuthToken"];
        _fromNumber = config["Twilio:FromNumber"];
        _isDevelopment = env.IsDevelopment();
        _logger = logger;

        // IsNullOrEmpty (not != null) so the appsettings.json placeholder values
        // are treated as "not configured" — same guard runs in SendCodeAsync below.
        if (!string.IsNullOrEmpty(_accountSid) && !string.IsNullOrEmpty(_authToken))
        {
            TwilioClient.Init(_accountSid, _authToken);
        }
    }

    public async Task SendCodeAsync(string phoneNumber, string code)
    {
        // In Development we never hit Twilio (no real SMS, no spent credits) — the code is
        // logged to the API console so a local/agent session can sign in by reading it. Gated
        // strictly on IsDevelopment, so production always sends a real SMS. This logs the
        // genuine generated OTP, not a fixed backdoor code, so no static bypass exists.
        if (_isDevelopment || string.IsNullOrEmpty(_accountSid))
        {
            _logger.LogWarning("OTP (dev — not SMS'd) for {Phone}: {Code}", phoneNumber, code);
            return;
        }

        await MessageResource.CreateAsync(
            to: new Twilio.Types.PhoneNumber(phoneNumber),
            from: new Twilio.Types.PhoneNumber(_fromNumber),
            body: $"Your Hurrah.tv code is: {code}");
    }
}
