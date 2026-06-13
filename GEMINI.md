# MANDATS DU PROJET TestingAi

Ce fichier contient les instructions fondamentales qui DOIVENT être respectées à chaque interaction.

## 1. PERSISTENCE & CONTEXTE (OBLIGATOIRE)
- **Source de Vérité** : La base de données SQLite TestingAi.db est le cerveau du projet.
- **Vérification Systématique** : À chaque prompt, vous devez savoir quelle tâche du Backlog est InProgress. Si aucune, consultez le Backlog pour la tâche prioritaire en Todo.
- **Historique** : Consultez les tables Sessions et ActivityLog pour ne pas répéter des actions déjà effectuées.

## 2. STANDARDS D''ARCHITECTURE (BACKEND .NET 8)
- **Structure en Couches** : Presentation (API) | Application (DI/Config) | Domain (Métier) | Infrastructure (Data).
- **Séparation Intf/Impl** : les couches Domain et Infrastructure DOIVENT avoir des sous-répertoires Intf (interfaces) et Impl (implémentations).
- **Patterns** : Service/Repository, DTOs pour l''API, AutoMapper, Dapper pour la Data.
- **Logique** : La couche Core est pure (pas de DTO, pas de DAL).

## 3. STANDARDS FRONTEND (ANGULAR)
- **Modularité** : Utilisation systématique du **Lazy Loading** pour les modules.

## 4. RÈGLES DE COMMUNICATION & QUALITÉ
- **Langue** : Tout le contenu textuel (hors code source technique) doit être en **Français** avec une orthographe et des accents impeccables.
- **Traçabilité** : Chaque appel à un outil de modification (write_file, replace, run_shell_command) doit être suivi ou accompagné d''une mise à jour dans la table ActivityLog.
- **Machine d''État** : Respectez scrupuleusement les transitions Todo -> InProgress -> Done.

## 5. AGNOSTICISME LLM
- Ne jamais lier le code à une API LLM spécifique. Utilisez toujours les interfaces d''abstraction définies dans le projet.
