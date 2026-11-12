using System;
using System.Diagnostics.CodeAnalysis;

namespace PoPunkouterSoftware.Shared;

/// <summary>
/// Represents the result of an operation, with success or error information.
/// Replaces null-returning patterns to surface errors to callers.
/// </summary>
/// <typeparam name="T">The type of the success value.</typeparam>
public readonly record struct Result<T>(bool IsSuccess, T? Value, string? Error, Exception? Exception)
{
    /// <summary>Creates a successful result with the given value.</summary>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Intentional static Result<T>.Success factory API.")]
    public static Result<T> Success(T value) => new(IsSuccess: true, Value: value, Error: null, Exception: null);

    /// <summary>Creates a failed result with the given error message and optional exception.</summary>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Intentional static Result<T>.Failure factory API.")]
    public static Result<T> Failure(string error, Exception? ex = null) =>
        new(IsSuccess: false, Value: default, Error: error, Exception: ex);
}
