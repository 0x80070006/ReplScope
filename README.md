<p align="center"><img src="docs/logo.png" width="128" alt="ReplScope"></p>

# ReplScope

Moniteur de réplication Active Directory moderne, inspiré de `replmon`, pour Windows Server 2022 à 2026.

- **Lecture seule** : aucune écriture dans l'annuaire, aucun identifiant stocké (Kerberos, compte courant).
- **Avancement en direct** : DC découverts immédiatement, état mis à jour au fil des réponses, barre de progression, journal horodaté, bouton Arrêter / Échap.
- **Arbre** Forêt → Site → DC → partitions, avec l'état le plus dégradé remonté sur les sites.
- **Colonnes** : source, site source, dernier succès, âge, échecs consécutifs, code d'erreur (hex), message. Tri par colonne, filtre texte.
- **Seuils d'alerte** réglables, mise à jour automatique (1 à 30 min), export CSV protégé contre l'injection de formules.
- Parallélisme borné (8 DC), délai de 30 s par DC, instance unique, DPI par moniteur.

## Utilisation

Téléchargez `ReplScope.exe` depuis les [Releases](../../releases) et lancez-le sur une machine jointe au domaine (compte de domaine standard suffisant). Exécutable autonome : aucun runtime .NET à installer.

## Compilation

```powershell
dotnet publish src/ReplScope.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

Nécessite le SDK .NET 10.

## Limites

L'API `System.DirectoryServices.ActiveDirectory` est synchrone : la progression est par DC, pas par partition. Les latences de réplication de bout en bout ne sont pas mesurées.
