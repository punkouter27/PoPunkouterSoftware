using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware.Unit;

public class SecretMaskingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("\t\r\n")]
    public void NullEmptyOrWhitespace_RendersNotSet(string? value)
    {
        SecretMasking.MaskValue(value).Should().Be("(not set)");
    }

    [Theory]
    [InlineData("12345678")] // exactly 8 chars — boundary of the fully-masked bucket
    public void EightCharsOrFewer_FullyMasked(string value)
    {
        SecretMasking.MaskValue(value).Should().Be("****");
    }

    [Theory]
    [InlineData("ABCDEFGHI", "ABCD*FGHI")]        // 9 chars → single star
    [InlineData("ABCDEFGHIJKL", "ABCD****IJKL")]  // 12 chars → four stars
    public void LongerValues_KeepFirstFourAndLastFour(string value, string expected)
    {
        SecretMasking.MaskValue(value).Should().Be(expected);
    }

    /// <summary>Both sides of the star cap — the last length under it and one far past it.</summary>
    [Fact]
    public void StarCount_TracksLength_AndIsCappedAtTwenty()
    {
        // 28 chars is the last length where stars (len - 8 = 20) still fit under the cap.
        SecretMasking.MaskValue(new string('x', 28))
            .Should().Be("xxxx" + new string('*', 20) + "xxxx");

        var masked = SecretMasking.MaskValue("HEAD" + new string('m', 992) + "TAIL"); // 1000 chars

        masked.Should().Be("HEAD" + new string('*', 20) + "TAIL",
            because: "the star run is capped at 20 regardless of input length");
        masked.Length.Should().Be(28);
    }

    /// <summary>
    /// The mask applied to /api/diag/report, which is anonymous and returns every resource id
    /// in the estate. The subscription GUID goes; the rest of the id stays, because the panel
    /// renders resource group and name and those are already public.
    /// </summary>
    [Fact]
    public void MaskSubscriptionIds_RemovesEveryGuid_AndKeepsTheRestOfTheId()
    {
        const string json = """
            {"a":"/subscriptions/14d963a2-badc-4a1f-9900-553bf01f8de2/resourceGroups/PoMode/providers/Microsoft.Web/sites/app-pomode2",
             "b":"/SUBSCRIPTIONS/BBB8DFBE-9169-432F-9B7A-FBF861B51037/resourceGroups/PoWatch/providers/Microsoft.Storage/storageAccounts/stpo"}
            """;

        var masked = SecretMasking.MaskSubscriptionIds(json);

        masked.Should().NotContain("bbb8dfbe", "the id must go regardless of casing");
        masked.Should().NotContain("BBB8DFBE");
        masked.Should().Contain("/subscriptions/****/resourceGroups/PoMode/providers/Microsoft.Web/sites/app-pomode2");
        masked.Should().Contain("/resourceGroups/PoWatch/providers/Microsoft.Storage/storageAccounts/stpo");

        // ...and everything that is not an ARM id passes through untouched, so the mask can
        // be applied to a whole serialized report without corrupting the rest of it.
        SecretMasking.MaskSubscriptionIds("""{"note":"no ids here","n":42}""")
            .Should().Be("""{"note":"no ids here","n":42}""");
        SecretMasking.MaskSubscriptionIds("").Should().Be("");
    }

    /// <summary>
    /// Both masks in one contract: a secret keeps its ends and loses its middle, an address
    /// loses the local part and keeps the domain. The email half backs /api/signins, which is
    /// anonymous on a site with no login and carries the addresses of real people.
    /// </summary>
    [Fact]
    public void MaskedValues_NeverExposeTheIdentifyingPart()
    {
        var masked = SecretMasking.MaskValue("AccountKey=SuperSecretValue123");

        masked.Should().NotContain("SuperSecret");
        masked.Should().StartWith("Acco");
        masked.Should().EndWith("e123");

        // The domain survives on purpose — "someone at a company" and "another throwaway
        // gmail" are the two readings /users exists to support. The local part does not.
        SecretMasking.MaskEmail("punkouter26@gmail.com").Should().Be("pu********6@gmail.com");
        SecretMasking.MaskEmail("yannick@lotus-diffusers.nl").Should().Be("ya****k@lotus-diffusers.nl");

        // The last character survives so NEIGHBOURING addresses stay distinguishable. Without
        // it these two real accounts collapsed to one string and the roster showed two
        // identical rows for two different people.
        SecretMasking.MaskEmail("punkouter27@gmail.com").Should()
            .NotBe(SecretMasking.MaskEmail("punkouter26@gmail.com"));

        // Star run capped, so a long local part cannot be counted back to its own length.
        SecretMasking.MaskEmail(new string('n', 60) + "@x.io").Should().Be("nn********n@x.io");

        // Degenerate inputs still mask rather than passing through: a local part too short to
        // reveal any of, and a value that is not an address at all.
        SecretMasking.MaskEmail("a@b.co").Should().Be("***@b.co");
        SecretMasking.MaskEmail("abcd@b.co").Should().Be("a**d@b.co");
        SecretMasking.MaskEmail("not-an-address").Should().Be("no********s");
        SecretMasking.MaskEmail(null).Should().Be("(unknown)");
        SecretMasking.MaskEmail("   ").Should().Be("(unknown)");
    }
}

