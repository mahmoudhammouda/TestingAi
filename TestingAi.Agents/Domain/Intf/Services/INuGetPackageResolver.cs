using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TestingAi.Agents.Domain.Intf.Services
{
    /// <summary>Référence de package NuGet (identifiant + version).</summary>
    public record NuGetPackageRef(string Id, string Version);

    /// <summary>Résultat de la résolution des dépendances d'un projet source.</summary>
    public record SourceResolveResult(
        bool Compiles,
        IReadOnlyList<NuGetPackageRef> Resolved,
        IReadOnlyList<string> Unresolved,
        string BuildOutput);

    /// <summary>
    /// Résout les dépendances NuGet d'un code source soumis afin qu'il COMPILE :
    /// certains espaces de noms ne font pas partie du framework partagé .NET 8
    /// (ex. TPL Dataflow) et exigent une référence de package. Le résolveur construit
    /// le projet source seul, lit les espaces de noms manquants signalés par le
    /// compilateur (CS0246), résout chaque dépendance (liste curée puis recherche NuGet)
    /// et réécrit le csproj jusqu'à ce que le projet compile.
    /// </summary>
    public interface INuGetPackageResolver
    {
        Task<SourceResolveResult> EnsureSourceCompilesAsync(string srcDir, CancellationToken ct = default);
    }
}
