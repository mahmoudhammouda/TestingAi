using System;
using System.IO;
using System.Threading.Tasks;

namespace TestingAi.Agents.Tests.E2eTests.Fixtures;

/// <summary>
/// Crée un vrai projet .NET xUnit temporaire sur le disque pour les tests E2e.
/// Le projet contient un test qui échoue (bug volontaire dans le code source).
/// </summary>
public class TempProjectFixture : IDisposable
{
    public string ProjectPath { get; }
    public string SourcePath { get; }
    public string BuggySourceFile { get; }
    public string TestFile { get; }

    public TempProjectFixture()
    {
        ProjectPath = Path.Combine(Path.GetTempPath(), $"e2e_proj_{Guid.NewGuid():N}");
        SourcePath = Path.Combine(ProjectPath, "Source");
        Directory.CreateDirectory(ProjectPath);
        Directory.CreateDirectory(SourcePath);

        BuggySourceFile = Path.Combine(ProjectPath, "Calculator.cs");
        TestFile = Path.Combine(ProjectPath, "CalculatorTests.cs");
    }

    /// <summary>
    /// Crée un projet avec un bug volontaire : Add retourne a - b au lieu de a + b.
    /// </summary>
    public async Task CreateBuggyProjectAsync()
    {
        await File.WriteAllTextAsync(BuggySourceFile, @"
namespace SampleProject;

public class Calculator
{
    public int Add(int a, int b) => a - b;  // BUG INTENTIONNEL : doit être a + b
}");

        await File.WriteAllTextAsync(TestFile, @"
using Xunit;
using SampleProject;

namespace SampleProject.Tests;

public class CalculatorTests
{
    [Fact]
    public void Add_TwoPositives_ReturnsCorrectSum()
    {
        var calc = new Calculator();
        Assert.Equal(5, calc.Add(2, 3));
    }
}");

        await File.WriteAllTextAsync(Path.Combine(ProjectPath, "SampleProject.Tests.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""xunit"" Version=""2.9.3"" />
    <PackageReference Include=""Microsoft.NET.Test.Sdk"" Version=""17.12.0"" />
    <PackageReference Include=""xunit.runner.visualstudio"" Version=""2.8.2"" />
  </ItemGroup>
</Project>");
    }

    /// <summary>
    /// Crée un projet avec tous les tests déjà verts (code correct dès le départ).
    /// </summary>
    public async Task CreatePassingProjectAsync()
    {
        await File.WriteAllTextAsync(BuggySourceFile, @"
namespace SampleProject;

public class Calculator
{
    public int Add(int a, int b) => a + b;  // code correct
}");

        await File.WriteAllTextAsync(TestFile, @"
using Xunit;
using SampleProject;

namespace SampleProject.Tests;

public class CalculatorTests
{
    [Fact]
    public void Add_TwoPositives_ReturnsCorrectSum()
    {
        var calc = new Calculator();
        Assert.Equal(5, calc.Add(2, 3));
    }
}");

        await File.WriteAllTextAsync(Path.Combine(ProjectPath, "SampleProject.Tests.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""xunit"" Version=""2.9.3"" />
    <PackageReference Include=""Microsoft.NET.Test.Sdk"" Version=""17.12.0"" />
    <PackageReference Include=""xunit.runner.visualstudio"" Version=""2.8.2"" />
  </ItemGroup>
</Project>");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(ProjectPath))
                Directory.Delete(ProjectPath, true);
        }
        catch
        {
            // Nettoyage best-effort
        }
    }
}
