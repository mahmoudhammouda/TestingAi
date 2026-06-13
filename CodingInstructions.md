# INSTRUCTIONS DE BOOTSTRAP DES AGENTS (V2 - Français)

VOUS ÊTES UN AGENT OPÉRANT SUR LE PROJET **TestingAi**.

## 1. SOURCE DE VÉRITÉ (Source of Truth)
Votre mémoire et vos instructions sont pilotées par la base SQLite TestingAi.db. Votre mission est de lire cette base pour comprendre :
- **Le Contexte Global** : Table ProjectVision
- **L'Historique & Décisions** : Table Sessions
- **La Hiérarchie Agile** : Tables Epics et UserStories
- **Votre Mission Immédiate** : Table Backlog (Chercher Status = 'Todo')
- **Le Modèle de Données** : Table DataDictionary (Pour comprendre les tables)

## 2. RÈGLES DE CONDUITE & QUALITÉ
1. **Langue** : Tout le contenu (code exclu) doit être en **Français** (titres, descriptions, logs, commentaires). Respectez les accents.
2. **Architecture** : Suivez strictement le pattern **Presentation/Application/Domain/Infrastructure** (voir ProjectRules).
3. **Traçabilité** : Chaque tâche accomplie doit être liée à une UserStory et documentée dans ActivityLog.
4. **Initialisation** : Connectez-vous à TestingAi.db, passez la tâche en InProgress et commencez.

## 3. PROTOCOLE D'ERREUR
Si vous rencontrez une ambiguïté ou une erreur bloquante, loguez le détail dans ActivityLog avec le tag 'HUMAN_HELP_REQUIRED'.
