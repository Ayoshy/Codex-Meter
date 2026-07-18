# Contribuer à Codex Meter

Merci de votre intérêt pour le projet.

## Principes

- conserver l'application en lecture seule ;
- ne jamais manipuler ni stocker les identifiants Codex ;
- éviter toute télémétrie et tout service réseau tiers ;
- garder l'interface compacte et accessible ;
- accompagner les changements de comportement par un test.

## Vérifications locales

```powershell
dotnet build .\CodexUsageTray.sln --configuration Release
dotnet run --project .\tests\CodexUsageTray.Tests\CodexUsageTray.Tests.csproj --configuration Release
dotnet list .\src\CodexUsageTray\CodexUsageTray.csproj package --vulnerable --include-transitive
```

Décrivez clairement le problème résolu, le comportement obtenu et les vérifications effectuées dans votre pull request.
