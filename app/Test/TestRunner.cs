using System;
using System.Linq;
using System.Reflection;
using Godot;

namespace Lemmix.App.Test;

// Tests that need the engine (nodes, transforms, scenes) run inside Godot: `-- --test` on the
// Mac, headless (make app-test). Every public static method marked [AppTest] in this assembly is
// run; a test fails by throwing. Prints one line per test and "[lemmix] tests done: <n> failed".
[AttributeUsage(AttributeTargets.Method)]
public sealed class AppTestAttribute : Attribute { }

public static class Check
{
    public static void True(bool cond, string what) { if (!cond) throw new Exception(what); }
    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }
    public static void Near(Vector3 expected, Vector3 actual, string what, float eps = 1e-4f)
    {
        if (expected.DistanceTo(actual) > eps) throw new Exception($"{what}: expected {expected}, got {actual}");
    }
}

public partial class TestRunner : Node
{
    public override void _Ready() => CallDeferred(nameof(RunAll));

    void RunAll()
    {
        string? only = OS.GetCmdlineUserArgs().SkipWhile(a => a != "--test").Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        var tests = typeof(TestRunner).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<AppTestAttribute>() != null)
            .Where(m => only == null || (m.DeclaringType!.Name + "." + m.Name).Contains(only, StringComparison.Ordinal))
            .OrderBy(m => m.DeclaringType!.Name + "." + m.Name, StringComparer.Ordinal).ToList();
        int failed = 0;
        foreach (var m in tests)
        {
            string name = m.DeclaringType!.Name + "." + m.Name;
            try { m.Invoke(null, null); GD.Print("[test] PASS " + name); }
            catch (TargetInvocationException e) { failed++; GD.Print("[test] FAIL " + name + ": " + e.InnerException?.Message); }
        }
        GD.Print($"[lemmix] tests done: {tests.Count} run, {failed} failed");
        GetTree().Quit(failed == 0 ? 0 : 1);
    }
}
