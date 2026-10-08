using System.Threading.Tasks;

namespace Grid.Auth.Tests;

/// <summary>Entry point. Run: dotnet run --project tests/Grid.Auth.Tests</summary>
public static class Program
{
    public static int Main()
    {
        AlgorithmsTests.Register();
        DtoTests.Register();
        StoreAndSessionTests.Register();
        HttpClientContractTests.Register();
        FlowTests.Register();

        return TestRunner.RunAll();
    }
}