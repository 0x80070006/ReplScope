<p align="center"><img src="docs/logo.png" width="128" alt="ReplScope"></p>

# ReplScope

Moniteur de réplication Active Directory moderne, inspiré de `replmon`, pour Windows Server 2022 à 2026.

- **Lecture seule** : aucune écriture dans l'annuaire, aucun identifiant stocké (Kerberos, compte courant).
- **Session locale ou poste hors domaine** : si la session Windows n'est pas un compte du domaine, ReplScope détecte le domaine de la machine et propose *Action > Se connecter en tant que…*. Le compte saisi reste en mémoire pour la session, il n'est jamais écrit ni journalisé.
- **Avancement en direct** : DC découverts immédiatement, état mis à jour au fil des réponses, barre de progression, journal horodaté, bouton Arrêter / Échap.
- **Arbre** Forêt → Site → DC → partitions, avec l'état le plus dégradé remonté sur les sites.
- **Colonnes** : source, site source, dernier succès, âge, échecs consécutifs, code d'erreur (hex), message. Tri par colonne, filtre texte.
- **Détection des changements** : le USN synchronisé de chaque lien est suivi. Quand un objet est répliqué (création d'un utilisateur, modification…), la ligne est surlignée avec le Δ USN, le journal et la barre d'état l'indiquent, et l'événement « Changements répliqués » est ajouté à l'historique. Mise à jour automatique activée par défaut (10 s à 30 min, 30 s par défaut).
- **Détail de ce qui est répliqué** : en sélectionnant un lien (ou un événement de l'historique), un volet liste les objets concernés (créé / modifié / supprimé, classe, nom, DN, USN), lus en lecture seule sur le DC source dans la plage de USN répliquée (500 objets max).
- **Barres de chargement** : colonne *Fraîcheur* (âge du dernier succès, vert / orange / rouge), colonne *Activité* avec une barre animée quand le DC rapporte une opération de réplication en cours ou en file, et une barre qui s'estompe sur 2 minutes après des objets répliqués.
- **Onglet Métriques** (selon le nœud sélectionné : forêt, site, DC ou partition) : nombre de DC et leur état, liens sains en %, alertes, échecs, échecs consécutifs, âge max et moyen du dernier succès, temps de réponse des DC, durée de la dernière collecte, DC le plus dégradé, tableau par DC et histogramme de tendance par collecte.
- **Onglet Historique** : réplications réussies, échecs (avec code d'erreur), passages en alerte, rétablissements, DC injoignables. Filtrable par nœud, type et texte, exportable en CSV. Conservé 30 jours dans `%LOCALAPPDATA%\ReplScope\history.json` (noms de DC, partitions et codes d'erreur uniquement, jamais d'identifiants), effaçable depuis l'onglet.
- **Seuils d'alerte** réglables, mise à jour automatique (1 à 30 min), export CSV protégé contre l'injection de formules.
- Parallélisme borné (8 DC), délai de 30 s par DC, instance unique, DPI par moniteur.

## Utilisation

Téléchargez `ReplScope.exe` depuis les [Releases](../../releases) et lancez-le sur une machine jointe au domaine (compte de domaine standard suffisant). Exécutable autonome : aucun runtime .NET à installer.

### Mode ligne de commande (supervision, tâche planifiée)

```powershell
ReplScope.exe --export C:\Supervision\repl.csv [--forest contoso.lan]
```

Utilise uniquement le compte de la session : aucun mot de passe n'est accepté en ligne de commande. Code de retour : `0` OK, `1` alerte, `2` échec de réplication, `3` erreur de collecte, `4` arguments invalides.

## Compilation

```powershell
dotnet publish src/ReplScope.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

Nécessite le SDK .NET 10.

## Limites

L'API `System.DirectoryServices.ActiveDirectory` est synchrone : la progression est par DC, pas par partition. Les latences de réplication de bout en bout ne sont pas mesurées.
