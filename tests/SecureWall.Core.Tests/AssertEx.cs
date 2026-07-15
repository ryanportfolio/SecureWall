using System.Collections;

namespace SecureWall.Core.Tests;

internal static class AssertEx
{
    internal static void True(bool condition, string? message = null)
    {
        if (!condition)
            throw new InvalidOperationException(message ?? "Expected condition to be true.");
    }

    internal static void False(bool condition, string? message = null) =>
        True(!condition, message ?? "Expected condition to be false.");

    internal static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message ?? $"Expected <{expected}> but got <{actual}>.");
    }

    internal static void SequenceEqual(IEnumerable expected, IEnumerable actual, string? message = null)
    {
        var expectedItems = expected.Cast<object?>().ToArray();
        var actualItems = actual.Cast<object?>().ToArray();
        if (!expectedItems.SequenceEqual(actualItems))
        {
            throw new InvalidOperationException(
                message ?? $"Expected [{string.Join(", ", expectedItems)}] but got [{string.Join(", ", actualItems)}].");
        }
    }

    internal static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
