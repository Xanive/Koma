# Koma

[English](README.md) · [Deutsch](README.de.md) · [Français](README.fr.md)

Un lecteur d’anime pour Windows avec **mise à l’échelle Anime4K en temps réel**. Gardez vos anime sur votre PC, ouvrez un épisode et profitez-en.

**[Télécharger v0.2.2-beta.1](https://github.com/Xanive/Koma/releases/tag/v0.2.2-beta.1)** · Windows 11 x64 · Installation ou ZIP portable

![Bibliothèque Koma](docs/screenshots/library.fr.png)

## Commencer à regarder

1. Installez Koma, ou extrayez le ZIP portable **en entier** et ouvrez `Koma.exe`. Tout le nécessaire est inclus.
2. Placez vos épisodes dans **Videos → Anime**, ou choisissez **Ajouter un dossier** pour une collection sur un autre disque.
3. Choisissez une série et lancez la lecture. Vous pouvez aussi glisser une vidéo ou un dossier dans Koma.

Vos fichiers restent à leur place. Koma mémorise votre progression.

<details>
<summary>Mettre Koma à jour</summary>

Fermez Koma et lancez le nouveau programme d’installation. Vos paramètres, bibliothèque, historique et file de téléchargements sont conservés.

Pour la version portable, extrayez dans un nouveau dossier et copiez votre ancien dossier `data` avant de lancer Koma. Les données protégées de compte et de téléchargement fonctionnent uniquement avec le même compte Windows.

Les téléchargements bêta ne sont pas signés : Windows peut afficher un avertissement sur l’éditeur. Les sommes de contrôle sont sur la page de la version.

</details>

## Fonctionnalités

- Bibliothèque par dossiers, avec saisons, illustrations et reprise de lecture.
- MKV et MP4, plusieurs pistes audio, sous-titres, chapitres et vitesse de lecture.
- Commandes plein écran qui s’effacent pendant le visionnage.
- Suivi MyAnimeList facultatif et téléchargements intégrés.
- Interface en français, allemand et anglais.

## Mise à l’échelle

Commencez par **Auto**. Il adapte la qualité si votre GPU a du mal à suivre. La mise à l’échelle modifie l’image pendant la lecture ; votre fichier vidéo reste intact.

| Mode | Point de départ |
| --- | --- |
| Auto | Recommandé au quotidien |
| A | Sources floues, souvent en 1080p |
| B | Restauration plus douce, souvent en 720p |
| C | Réduction du bruit et mise à l’échelle, souvent en 480p |
| Off | Lecture originale |

Choisissez le rendu qui vous plaît. **… → Comparer à l’original** permet de regarder de plus près. En cas de saccades, fermez la comparaison et essayez Auto ou Off.

## Commandes

| Touche | Action |
| --- | --- |
| ← / → | Reculer / avancer |
| ↑ / ↓ | Volume |
| Espace | Lecture / pause |
| F ou F11 | Plein écran |
| Échap | Quitter le plein écran |
| M | Couper le son |
| Ctrl+← / Ctrl+→ | Épisode précédent / suivant |

Utilisez **Audio** et **CC** pour changer de piste. Dans **Paramètres**, réglez les pas de déplacement et de volume, les langues et le délai de masquage en plein écran.

## Suivi et téléchargements

**MyAnimeList :** connectez votre compte avec votre propre identifiant client enregistré, puis activez le suivi automatique. Les correspondances claires sont reconnues ; en cas de doute, choisissez une fois la bonne série. Les mises à jour exigent **80 % réellement visionnés**. Les mises à jour de progression sur de vrais comptes restent à valider. [Configurer le suivi](docs/Tracking.md) · [Choisir le bon anime](docs/Recognition.md)

**Téléchargements :** recherchez sur Nyaa ou ajoutez un lien magnet ou torrent. Les épisodes terminés apparaissent dans la bibliothèque. Les téléchargements inachevés restent en pause à la réouverture. [Guide des téléchargements](docs/Downloads.md)

## Aide et crédits

Bibliothèque vide ? Vérifiez votre dossier et choisissez **Actualiser la bibliothèque**. Pour un autre problème, [signalez un bug](https://github.com/Xanive/Koma/issues/new/choose) avec votre version de Koma et une brève description.

Koma a été entièrement développé en **vibe coding avec Codex**. La mise à l’échelle vient d’**[Anime4K de bloc97 et des contributeurs](https://github.com/bloc97/Anime4K)**. La lecture utilise [mpv](https://mpv.io/) et l’interface [WPF UI](https://github.com/lepoco/wpfui).

Le code de Koma est sous [GPL-3.0-or-later](LICENSE). [Composants tiers](THIRD-PARTY.md) · [Compiler Koma](docs/Engine-provenance.md) · [Tests des versions](docs/Release-testing.md)

<details>
<summary>Autres captures d’écran</summary>

![Paramètres](docs/screenshots/settings.png)

![Saisons et épisodes](docs/screenshots/episodes.png)

![Commandes de lecture](docs/screenshots/controls.png)

Ces captures montrent la bêta 5 avec des collections fictives et des vidéos de test générées.

</details>
