using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace TestingAi.Agents.Domain.Impl.Services
{
    // Capture le contenu des fichiers .cs (source + tests) d'une session sous forme de
    // JSON : { "sourceFiles": [{ "fileName", "content" }], "testFiles": [...] }.
    //
    // Pourquoi : l'onglet « Code » lit le code sur le disque, dans le dossier de travail
    // de la session. On persiste en plus cet instantané en base pendant le pipeline et
    // l'endpoint l'utilise en repli si le dossier de travail venait à disparaître
    // (filet de sécurité — à l'origine, ces dossiers étaient sous /tmp, purgé par le
    // système, ce qui vidait l'onglet ; ils sont désormais persistants sous Data/Sessions).
    public static class CodeSnapshotHelper
    {
        private static bool NotBuildArtifact(string f)
        {
            var sep = Path.DirectorySeparatorChar;
            return !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}");
        }

        private static List<object> ReadCsFiles(string? dir)
        {
            var files = new List<object>();
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                foreach (var path in Directory
                             .GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                             .Where(NotBuildArtifact)
                             .OrderBy(f => f))
                    files.Add(new { fileName = Path.GetFileName(path), content = File.ReadAllText(path) });
            return files;
        }

        // Lit les fichiers .cs des dossiers source/tests et renvoie le JSON correspondant
        // (forme identique à celle attendue par l'UI) ainsi qu'un indicateur précisant si
        // au moins un fichier a été trouvé sur le disque.
        public static (string Json, bool HasAny) CaptureJson(string? sourceDir, string? testDir)
        {
            var sourceFiles = ReadCsFiles(sourceDir);
            var testFiles = ReadCsFiles(testDir);
            var json = JsonConvert.SerializeObject(new { sourceFiles, testFiles });
            return (json, sourceFiles.Count > 0 || testFiles.Count > 0);
        }
    }
}
