internal static class Program
{
    private static async Task<int> Main()
    {
        int failed = 0;
        foreach (var (name, run) in PipeTests.Cases)
        {
            try { await run(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}\n{ex}"); }
        }
        Console.WriteLine($"RESULT {PipeTests.Cases.Length - failed}/{PipeTests.Cases.Length} passed");
        return failed == 0 ? 0 : 1;
    }

    internal static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
