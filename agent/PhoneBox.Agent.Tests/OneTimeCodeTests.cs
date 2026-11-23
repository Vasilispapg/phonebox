using PhoneBox.Agent;
using Xunit;

/// <summary>
/// Finding a login code in a message so the agent can put it on the clipboard. It must copy only when it is sure: one
/// plausible code next to a keyword. A wrong copy overwrites what the owner had copied, so doubt means no copy.
/// </summary>
public sealed class OneTimeCodeTests
{
    [Theory]
    [InlineData("Your verification code is 482913", "482913")]
    [InlineData("482913 is your Google verification code", "482913")]
    [InlineData("Ο κωδικός σας είναι 482913", "482913")]
    [InlineData("ΚΩΔΙΚΟΣ 482913", "482913")]
    [InlineData("Κωδικός: 482-913", "482913")]
    [InlineData("Your code is 4829 13", "482913")]
    [InlineData("Your OTP: 7391", "7391")]
    [InlineData("PIN 12345678 for your transfer", "12345678")]
    [InlineData("Το PIN σου: 5821", "5821")]
    [InlineData("Code 482913. Again: your code 482913", "482913")]   // the same code twice is one code
    [InlineData("Use 482913 to verify your account.\nIgnore this line", "482913")]
    public void OneCodeNextToAKeywordIsFound(string text, string expected) => Assert.Equal(expected, OneTimeCode.Find(text));

    [Theory]
    [InlineData("Your PIN is 1234 and code 5678")]                       // two codes: do not guess
    [InlineData("Order 483920 shipped. Code: 123456")]                   // two numbers near a keyword
    [InlineData("Your balance is 123456 EUR")]                           // a number but no keyword
    [InlineData("Call me on 2105551234 code")]                           // ten digits are a phone number
    [InlineData("Call 210-555-1234 about your code")]                    // a phone number written with dashes
    [InlineData("Your code review is on 2026-11-23")]                    // a date
    [InlineData("code A1234B")]                                          // digits glued to letters
    [InlineData("code 123")]                                             // too short
    [InlineData("code 123456789")]                                       // too long
    [InlineData("Your code is ready")]                                   // a keyword but no digits
    [InlineData("pinterest board 4829")]                                 // 'pin' inside another word is not the keyword
    [InlineData("Your code is soon.\n\n482913 other text")]              // the keyword is on another line
    [InlineData("")]
    public void AnythingDoubtfulCopiesNothing(string text) => Assert.Null(OneTimeCode.Find(text));

    [Fact]
    public void TheKeywordMustBeCloseToTheCode()
    {
        var far = "Your verification code arrives by " + new string('x', 5) + " mail soon, after a long wait of many minutes, 482913";
        Assert.Null(OneTimeCode.Find(far));
    }

    [Fact]
    public void GreekCapitalsAndFinalSigmaAndAccentsAllMatch()
    {
        Assert.Equal("123456", OneTimeCode.Find("ΚΩΔΙΚΟΣ ΕΠΑΛΗΘΕΥΣΗΣ: 123456"));
        Assert.Equal("123456", OneTimeCode.Find("κωδικοσ 123456"));
        Assert.Equal("123456", OneTimeCode.Find("Ο κωδικός επαλήθευσης είναι 123456"));
        Assert.Equal("123456", OneTimeCode.Find("123456 είναι ο κωδικός σου"));
    }
}
