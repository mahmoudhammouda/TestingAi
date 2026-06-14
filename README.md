# TestingAi

Système multi-agents de supervision et correction automatique de tests unitaires .NET, piloté par l'IA.

## Ce que fait ce projet

TestingAi analyse un projet de tests C# et exécute un pipeline multi-agents qui :
1. **Découvre** tous les tests (xUnit, MSTest, NUnit) via regex
2. **Exécute** les tests avec `dotnet test` et parse les résultats TRX (vert / rouge)
3. **Décide** de l'action pour chaque test rouge : `FixTest` / `FixCode` / `Ignore`
4. **Corrige** automatiquement via un LLM (Gemini 2.0 Flash ou GPT-4o)
5. **Vérifie** que la correction fonctionne — boucle jusqu'à N tentatives (défaut : 3)

Un **mode intervention humaine** permet à un opérateur de valider chaque décision avant que l'IA n'agisse.

## Architecture

```
TestingAi.Agents/
  Domain/
    Impl/
      Models/
        TestCase.cs             — Modèles de données : TestCase, PipelineSettings, enums
        AgentState.cs           — État partagé entre agents
        RuntimeModels.cs        — Modèles SQLite (sessions, logs)
      Services/
        TestDiscoveryAgent.cs   — Découverte des tests via regex sur les fichiers .cs
        TestRunnerAgent.cs      — Exécution dotnet test + parsing TRX
        TestDecisionAgent.cs    — Décision IA (Gemini) ou mode humain
        TestFixerAgent.cs       — Correction du code via LLM
        AgentOrchestrator.cs    — Pipeline complet avec pause/reprise humaine
    Intf/
      Services/
        IDbContext.cs           — Interface de persistence (sessions, tests, settings)
        IAgentOrchestrator.cs   — Interface de l'orchestrateur
  Infrastructure/
    Impl/
      DbContext.cs              — SQLite via Dapper (5 tables)
      GeminiLlmProvider.cs      — Provider Gemini 2.0 Flash (v1beta)
      OpenAiLlmProvider.cs      — Provider OpenAI GPT-4o
      ProcessRunner.cs          — Exécution de processus système

TestingAi.Api/
  Program.cs                    — API REST ASP.NET 8 Minimal API

TestingAi.ui/
  src/app/
    dashboard/                  — Interface Angular de supervision (dark mode)
    services/api.service.ts     — Client HTTP
    models/test-models.ts       — Types TypeScript
```

## Démarrage rapide

### Prérequis
- .NET 8 SDK
- Node.js 18+, Angular CLI

### 1. Clé API Gemini (ou OpenAI)
```bash
export GEMINI_API_KEY=votre_clé
# ou dans appsettings.json → "GeminiApiKey": "..."
```

### 2. Lancer l'API
```bash
cd TestingAi.Api
dotnet run
# → http://localhost:5182/swagger
```

### 3. Lancer l'UI
```bash
cd TestingAi.ui
npm install
npm start
# → http://localhost:4200
```

## API REST

| Méthode | Route | Description |
|---------|-------|-------------|
| GET | `/api/sessions` | Liste toutes les sessions |
| POST | `/api/sessions` | Démarre un nouveau pipeline |
| GET | `/api/sessions/{id}` | Détails d'une session |
| POST | `/api/sessions/{id}/resume` | Reprend après décisions humaines |
| GET | `/api/sessions/{id}/tests` | Tests d'une session |
| PUT | `/api/tests/{id}/action` | Définit l'action (FixTest/FixCode/Ignore) |
| PUT | `/api/tests/bulk-action` | Action groupée |
| GET/PUT | `/api/settings` | Paramètres du pipeline |

## Mode intervention humaine

**Désactivé (défaut)** : l'IA décide et corrige sans interruption.

**Activé** :
1. Les tests rouges passent en `EnAttenteDecision`
2. Le pipeline se suspend
3. L'opérateur définit l'action via l'UI ou l'API
4. Clic sur **Reprendre** → l'IA applique les corrections

## Stack
- **Backend** : ASP.NET 8, Dapper, SQLite
- **LLM** : Gemini 2.0 Flash (`v1beta`) ou GPT-4o
- **Frontend** : Angular 17 standalone, TypeScript
