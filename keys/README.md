# SSH key backup

Public key backed up in this repo (safe to commit and add to GitHub):

```text
keys/cleanswitch-github.pub
```

GitHub → Settings → SSH and GPG keys → New SSH key → paste that file.

The private key is also copied locally as `ssh/id_ed25519` for backup on
this PC. Git is set to ignore that file so it is not pushed to GitHub.

The live key pair on this PC:

```text
%USERPROFILE%\.ssh\id_ed25519
%USERPROFILE%\.ssh\id_ed25519.pub
```

Do not push `id_ed25519`. Anyone with that file can use this GitHub key.
