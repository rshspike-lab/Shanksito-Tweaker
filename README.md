# Shanksito Tweaker

Opti by claude made with ❤️

Optimiseur Windows pour Valorant, avec un assistant IA qui tourne entièrement sur le PC.

- Diagnostic du PC et score de configuration
- Réglages Windows, réseau, démarrage, services et tâches planifiées, tous annulables
- Session de jeu : vérifications, fermeture des applis, lancement de Valorant, rapport de partie
- Performances mesurées depuis les logs de Valorant, avec comparaison avant/après
- Tests réseau : ping, ping sous charge, trajet jusqu'aux serveurs
- Santé du PC et nettoyage
- Shanksito AI : modèle local au choix, téléchargé depuis l'app et vérifié par SHA-256

Le jeu et son anti-triche ne sont jamais touchés. Projet indépendant, non affilié à Riot Games, NVIDIA ou ASUS.

## Télécharger

Gratuit, sans compte et sans publicité.

1. Va sur la page [Releases](https://github.com/rshspike-lab/shanksito-tweaker/releases/latest).
2. Télécharge `ShanksitoTweaker-Setup.exe` : Windows 10 ou 11, 64 bits, rien d'autre à installer.
3. Lance-le. L'exe n'est pas signé, donc Windows peut afficher « Windows a protégé votre PC » : clique **Informations complémentaires**, puis **Exécuter quand même**.

Le code est ici : tu peux le lire et compiler l'app toi-même.

Chaque réglage est sauvegardé et annulable, mais l'outil modifie Windows : tu l'utilises à tes risques.

## Compiler

Il faut Windows et le SDK .NET 9.

```bash
dotnet publish ShanksitoTweaker.csproj -c Release -o release/final
```

Le résultat est un seul exe autonome, qui n'a pas besoin de .NET sur le PC cible.
Si son nom contient `setup`, il s'ouvre en installateur. Sinon il lance directement l'app.

## Tester

Ces modes tournent sans fenêtre. Le premier chemin reçoit le résultat.

```bash
ShanksitoTweaker.exe --selftest resultat.txt
ShanksitoTweaker.exe --scan rapport.txt
ShanksitoTweaker.exe --shot dossier-images
ShanksitoTweaker.exe --install-test dossier
ShanksitoTweaker.exe --watch-test resultat.txt
ShanksitoTweaker.exe --ai-test dossier qwen35-4b
```

`--selftest` n'écrit rien sur la machine : tous les réglages passent par un système simulé.
`--ai-test` télécharge le moteur et le modèle indiqué dans `dossier`.
