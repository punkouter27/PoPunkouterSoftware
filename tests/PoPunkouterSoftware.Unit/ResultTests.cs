using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Unit;

// Trimmed to the budget (the old 100-test Unit budget). Result<T> is a four-property carrier, and
// this file used twelve one-assertion Facts to describe it — Success_SetsIsSuccessTrue,
// Success_SetsValue, Success_ErrorIsNull and Success_ExceptionIsNull all constructed the
// same value and looked at a different field of it. Asserting the WHOLE shape of each
// factory in one test is the same coverage and names the actual contract: a success
// carries a value and no error, a failure carries an error and no value.

public class ResultTests
{
    [Fact]
    public void Success_CarriesTheValue_AndNoError()
    {
        var result = Result<int>.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
        result.Error.Should().BeNull();
        result.Exception.Should().BeNull();
    }

    [Fact]
    public void Failure_CarriesTheError_AndNoValue()
    {
        var boom = new InvalidOperationException("boom");

        var result = Result<string>.Failure("storage unavailable", boom);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("storage unavailable");
        result.Exception.Should().BeSameAs(boom);
        result.Value.Should().BeNull();

        // The degradation paths report a message with no exception far more often than
        // they report one with — "no report found" is not an error condition.
        Result<string>.Failure("no report").Exception.Should().BeNull();
    }

    [Fact]
    public void Success_AcceptsNullValue_WithoutBecomingAFailure()
    {
        // AzureReportStore.LoadAsync returns Success(null) for "storage is fine, there is
        // simply no report yet" — the callers branch on IsSuccess, so conflating this with
        // failure would turn an empty store into a 503.
        var result = Result<string?>.Success(null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void Success_WithReferenceType_DoesNotCopyTheValue()
    {
        var list = new List<string> { "a", "b" };

        Result<List<string>>.Success(list).Value.Should().BeSameAs(list);
    }
}
