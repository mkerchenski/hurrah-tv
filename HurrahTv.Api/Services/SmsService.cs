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

        if (_accountSid != null && _authToken != null)
        {
            TwilioClient.Init(_accountSid, _authToken);
        }
    }

    public async Task SendCodeAsync(string phoneNumber, string code)
    {
        if (_accountSid == null)
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
