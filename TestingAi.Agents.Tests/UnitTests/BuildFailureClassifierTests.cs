using System.Collections.Generic;
using TestingAi.Agents.Domain.Impl.Services;
using Xunit;

namespace TestingAi.Agents.Tests.UnitTests;

/// <summary>
/// Tests unitaires du <see cref="BuildFailureClassifier"/> : c'est lui qui décide si un
/// échec de build justifie une ADAPTATION DE LA SOURCE (accessibilité) ou non. Il ne doit
/// JAMAIS déclencher d'adaptation pour des bugs purement situés dans le code de test généré.
/// </summary>
public class BuildFailureClassifierTests
{
    [Fact]
    public void CS0122_ImpliqueAccessibiliteSource()
    {
        var errors = new List<string>
        {
            "/tmp/x/Src.Tests/CalculatorTests.cs(12,21): error CS0122: 'Calculator' is inaccessible due to its protection level [/tmp/x/Src.Tests/Src.Tests.csproj]"
        };
        Assert.True(BuildFailureClassifier.ImplicatesSourceAccessibility(errors));
    }

    [Fact]
    public void LibelleInaccessible_ImpliqueAccessibiliteSource()
    {
        var errors = new List<string> { "error: 'Program' is inaccessible due to its protection level" };
        Assert.True(BuildFailureClassifier.ImplicatesSourceAccessibility(errors));
    }

    [Fact]
    public void ErreursPurementCoteTest_NImpliquentPasLaSource()
    {
        // CS0411 / CS1503 : bugs typiques du test généré (inférence de générique, conversion
        // d'argument) — la source n'est PAS en cause, donc aucune adaptation ne doit avoir lieu.
        var errors = new List<string>
        {
            "CalculatorTests.cs(20,9): error CS0411: The type arguments for method cannot be inferred from the usage.",
            "CalculatorTests.cs(31,40): error CS1503: Argument 2: cannot convert from 'int' to 'string'"
        };
        Assert.False(BuildFailureClassifier.ImplicatesSourceAccessibility(errors));
    }

    [Fact]
    public void ErreursMixtes_AvecBlocageAccessibilite_ImpliquentLaSource()
    {
        // Si un blocage d'accessibilité de la source coexiste avec des bugs de test,
        // l'adaptation reste pertinente (la régénération post-adaptation peut lever les bugs secondaires).
        var errors = new List<string>
        {
            "CalculatorTests.cs(20,9): error CS0411: The type arguments for method cannot be inferred from the usage.",
            "CalculatorTests.cs(12,21): error CS0122: 'Calculator' is inaccessible due to its protection level"
        };
        Assert.True(BuildFailureClassifier.ImplicatesSourceAccessibility(errors));
    }

    [Fact]
    public void ListeVideOuNull_NImpliquePasLaSource()
    {
        Assert.False(BuildFailureClassifier.ImplicatesSourceAccessibility(null));
        Assert.False(BuildFailureClassifier.ImplicatesSourceAccessibility(new List<string>()));
        Assert.False(BuildFailureClassifier.ImplicatesSourceAccessibility(new List<string> { "", "   " }));
    }
}
