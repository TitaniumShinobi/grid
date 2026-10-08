using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Grid.Auth.Tests;

/// <summary>Minimal assertion + runner. No test framework dependency.</summary>
public static class Assert
{
    public static void True(bool condition, string name)
    {
        if (!condition) throw new Exception($"FAILED: {name}");
    }

    public static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"FAILED: {name}\n  expected: {expected}\n  actual:   {actual}");
        }
    }

    public static void NotNull(object? value, string name)
    {
        if (value is null) throw new Exception($"FAILED: {name} (expected non-null)");
    }

    public static void Null(object? value, string name)
    {
        if (value is not null) throw new Exception($"FAILED: {name} (expected null)");
    }

    public static TException Throws<TException>(Action action, string name)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException e)
        {
            return e;
        }
        throw new Exception($"FAILED: {name} (expected {typeof(TException).Name})");
    }
}

public static class TestRunner
{
    public sealed record TestCase(string Suite, string Name, Func<Task> Run);

    private static readonly List<TestCase> Tests = new();

    public static void Add(string suite, string name, Func<Task> run) => Tests.Add(new TestCase(suite, name, run));

    public static int RunAll()
    {
        var passed = 0;
        var failures = new List<(string Suite, string Name, string Error)>();
        foreach (var test in Tests)
        {
            try
            {
                test.Run().GetAwaiter().GetResult();
                passed++;
                Console.WriteLine($"PASS  [{test.Suite}] {test.Name}");
            }
            catch (Exception e)
            {
                failures.Add((test.Suite, test.Name, e.Message));
                Console.WriteLine($"FAIL  [{test.Suite}] {test.Name}");
                Console.WriteLine($"      {e.Message}");
            }
        }
        Console.WriteLine();
        Console.WriteLine($"== {Tests.Count} tests, {passed} passed, {failures.Count} failed ==");
        foreach (var (suite, name, error) in failures)
        {
            Console.WriteLine($"  FAILED [{suite}] {name}: {error}");
        }
        return failures.Count == 0 ? 0 : 1;
    }
}