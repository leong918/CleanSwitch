# SSH backup

This folder is a local backup of the CleanSwitch PC GitHub SSH key.

| File | What it is | GitHub |
|---|---|---|
| `id_ed25519.pub` | Public key. Paste this into GitHub. | Safe to keep in the repo |
| `id_ed25519` | Private key. Do not share. | Never push this file |

The live key pair also exists at:

```text
C:\Users\cleanswitch\.ssh\id_ed25519
C:\Users\cleanswitch\.ssh\id_ed25519.pub
```

## Add the public key to GitHub

1. GitHub → **Settings** → **SSH and GPG keys** → **New SSH key**
2. Title: `CleanSwitch PC`
3. Paste the contents of `id_ed25519.pub`
4. Save

Then use:

```text
git@github.com:USER/REPO.git
```
