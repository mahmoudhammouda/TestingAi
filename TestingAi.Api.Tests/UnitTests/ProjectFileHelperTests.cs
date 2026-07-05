using System.Collections.Generic;
using Xunit;

namespace TestingAi.Api.Tests.UnitTests;

/// <summary>
/// Tests unitaires de la détection du framework cible (CAS 2 de l'import de dossier).
/// Logique statique pure : on verrouille le parsing, le scoring/sélection et la réécriture
/// mono-cible sans dépendre du pipeline LLM ni d'un vrai build.
/// </summary>
public class ProjectFileHelperTests
{
    private static string Csproj(string element, string value) =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><{element}>{value}</{element}></PropertyGroup></Project>";

    private static string Single(string tfm) => Csproj("TargetFramework", tfm);
    private static string Multi(string tfms) => Csproj("TargetFrameworks", tfms);

    // ── ParseTargetFrameworks ────────────────────────────────────────────────
    [Fact]
    public void Parse_Single_ReturnsOne()
    {
        Assert.Equal(new[] { "net8.0" }, ProjectFileHelper.ParseTargetFrameworks(Single("net8.0")));
    }

    [Fact]
    public void Parse_Multi_SplitsAndTrims()
    {
        Assert.Equal(
            new[] { "net6.0", "net8.0", "netstandard2.0" },
            ProjectFileHelper.ParseTargetFrameworks(Multi(" net6.0 ; net8.0 ; netstandard2.0 ")));
    }

    [Fact]
    public void Parse_None_ReturnsEmpty()
    {
        Assert.Empty(ProjectFileHelper.ParseTargetFrameworks("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"));
    }

    // ── ResolveBuildTarget : cibles prises en charge ─────────────────────────
    [Theory]
    [InlineData("net8.0", "net8.0")]
    [InlineData("net7.0", "net7.0")]
    [InlineData("net6.0", "net6.0")]
    [InlineData("net5.0", "net5.0")]
    [InlineData("netstandard2.1", "netstandard2.1")]
    [InlineData("netstandard2.0", "netstandard2.0")]
    [InlineData("netcoreapp3.1", "netcoreapp3.1")]
    public void Resolve_SupportedSingle_ChoosesIt(string tfm, string expected)
    {
        var err = ProjectFileHelper.ResolveBuildTarget(Single(tfm), out var chosen);
        Assert.Null(err);
        Assert.Equal(expected, chosen);
    }

    // ── ResolveBuildTarget : multi-ciblage → meilleur score (le plus proche de net8) ──
    [Theory]
    [InlineData("net6.0;net8.0", "net8.0")]
    [InlineData("net6.0;net7.0", "net7.0")]
    [InlineData("net6.0;net9.0", "net6.0")]          // net9.0 ignoré (SDK trop ancien)
    [InlineData("net6.0;net8.0;net9.0", "net8.0")]
    [InlineData("netstandard2.0;net48", "netstandard2.0")] // net48 ignoré (.NET Framework)
    [InlineData("net8.0;net8.0-windows", "net8.0")]  // cible OS ignorée
    public void Resolve_Multi_PicksHighestSupported(string tfms, string expected)
    {
        var err = ProjectFileHelper.ResolveBuildTarget(Multi(tfms), out var chosen);
        Assert.Null(err);
        Assert.Equal(expected, chosen);
    }

    // ── ResolveBuildTarget : cibles refusées ─────────────────────────────────
    [Theory]
    [InlineData("net9.0")]           // SDK trop ancien
    [InlineData("net10.0")]
    [InlineData("net48")]            // .NET Framework (Windows)
    [InlineData("net472")]
    [InlineData("net8.0-windows")]   // cible OS-spécifique
    [InlineData("net6.0-android")]
    [InlineData("netstandardfoo")]   // valeur malformée
    public void Resolve_Unsupported_ReturnsFrenchError(string tfm)
    {
        var err = ProjectFileHelper.ResolveBuildTarget(Single(tfm), out var chosen);
        Assert.NotNull(err);
        Assert.Equal(string.Empty, chosen);
    }

    [Fact]
    public void Resolve_MultiAllUnsupported_ReturnsError()
    {
        var err = ProjectFileHelper.ResolveBuildTarget(Multi("net9.0;net48"), out var chosen);
        Assert.NotNull(err);
        Assert.Equal(string.Empty, chosen);
    }

    [Fact]
    public void Resolve_Wpf_ReturnsError()
    {
        var wpf = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                  "<TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF>" +
                  "</PropertyGroup></Project>";
        var err = ProjectFileHelper.ResolveBuildTarget(wpf, out _);
        Assert.NotNull(err);
    }

    [Fact]
    public void Resolve_NoTargetFramework_ReturnsError()
    {
        var err = ProjectFileHelper.ResolveBuildTarget("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>", out _);
        Assert.NotNull(err);
    }

    // ── RewriteToSingleTfm ───────────────────────────────────────────────────
    [Fact]
    public void Rewrite_Multi_BecomesSingleChosen()
    {
        var rewritten = ProjectFileHelper.RewriteToSingleTfm(Multi("net6.0;net9.0"), "net6.0");
        Assert.Contains("<TargetFramework>net6.0</TargetFramework>", rewritten);
        Assert.DoesNotContain("TargetFrameworks", rewritten);
        Assert.DoesNotContain("net9.0", rewritten);
    }

    [Fact]
    public void Rewrite_Single_IsUnchanged()
    {
        var input = Single("net6.0");
        Assert.Equal(input, ProjectFileHelper.RewriteToSingleTfm(input, "net6.0"));
    }
}
