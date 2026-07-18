# Politique de sécurité

## Versions prises en charge

Seule la dernière release de Codex Meter reçoit des correctifs de sécurité.

## Signaler une vulnérabilité

Merci de ne pas ouvrir d'issue publique pour une vulnérabilité non corrigée.

Utilisez de préférence **Security → Report a vulnerability** sur GitHub afin d'envoyer un rapport privé. Incluez :

- la version concernée ;
- les étapes de reproduction ;
- l'impact observé ou potentiel ;
- toute proposition de correction utile.

Une première réponse sera donnée dans la mesure du possible sous sept jours. La publication coordonnée du problème interviendra après la disponibilité d'un correctif.

## Périmètre de confiance

Codex Meter dialogue uniquement avec l'installation locale de Codex et lit les compteurs présents sous le profil Windows courant. Il ne doit jamais demander, enregistrer ou transmettre de clé API, cookie, access token, refresh token, prompt ou réponse.

Tout comportement contraire à cette garantie est considéré comme une vulnérabilité.
