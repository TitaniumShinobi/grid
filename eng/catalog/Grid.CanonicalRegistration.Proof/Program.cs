if (args.Length != 2)
    throw new ArgumentException("Usage: Grid.CanonicalRegistration.Proof <repository-root> <proof-output>");
var checks = await CanonicalRegistrationEngineChecks.RunAsync(args[0], args[1]);
Console.WriteLine($"PASS: {checks} independent registration checks; candidate only, NOT_PUBLISHED.");
