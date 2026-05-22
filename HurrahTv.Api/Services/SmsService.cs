using Twilio;
using Twilio.Rest.Api.V2010.Account;

namespace HurrahTv.Api.Services;

public class SmsService
{
    private readonly string? _accountSid;
    private readonly string? _authToken;
    private readonly string? _fromNumber;
    private readonly ILogger<SmsService> _logger;

    public SmsService(IConfiguration config, ILogger<SmsService> logger)
    {
        _accountSid = config["Twilio:AccountSid"];
        _authToken = config["Twilio:AuthToken"];
        _fromNumber = config["Twilio:FromNumber"];
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
        if (string.IsNullOrEmpty(_accountSid))
        {
            _logger.LogWarning("Twilio not configured — OTP for {Phone}: {Code}", phoneNumber, code);
            return;
        }

        await MessageResource.CreateAsync(
            to: new Twilio.Types.PhoneNumber(phoneNumber),
            from: new Twilio.Types.PhoneNumber(_fromNumber),
            body: $"Your Hurrah.tv code is: {code}");
    }
}
