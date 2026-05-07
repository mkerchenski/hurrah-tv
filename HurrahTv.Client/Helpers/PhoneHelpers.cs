namespace HurrahTv.Client.Helpers;

public static class PhoneHelpers
{
    // E.164 (+12345678900) or 10-digit US (2345678900) → "(234) 567-8900".
    // unrecognized formats pass through unchanged so non-US numbers don't get mangled.
    public static string Format(string phone)
    {
        string digits = new([.. phone.Where(char.IsDigit)]);
        if (digits.Length == 11 && digits[0] == '1') digits = digits[1..];
        if (digits.Length == 10) return $"({digits[..3]}) {digits[3..6]}-{digits[6..]}";
        return phone;
    }
}
