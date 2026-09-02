# Deploying

The public instance is <https://dotsmoke.wastebook.dev>. Every merge to `main`
publishes an image and deploys it: one DigitalOcean droplet, kamal-proxy on :80 and
:443 with a Let's Encrypt certificate it renews itself, forwarding to the app.

`config/deploy.yml` is the whole configuration, and its comments say why each value
is what it is.

## By hand

The route for a rollback, or for watching a deploy that is behaving oddly. Kamal is
a Ruby gem rather than anything this repo builds:

```bash
gem install kamal -v 2.12.0
export KAMAL_REGISTRY_PASSWORD=$(gh auth token)
kamal deploy --skip-push --version=$(git rev-parse main)
```

`--skip-push` is what makes this work on an image Kamal did not build: it pulls the
tag CI pushed, so the bytes that were attested are the bytes that run.

## Rolling back

Reverting the commit and merging is the normal route, through the same gate as
everything else. The faster one:

```bash
kamal rollback <earlier-commit>
```

Images are tagged by commit and Kamal keeps recent containers on the host, so this
does not work for a commit whose image was pruned.

## Setting up a new server

Rare enough to be a procedure rather than a workflow step. Create the droplet,
point the `A` record at it, put the deploy key in its `authorized_keys`, then:

```bash
kamal server bootstrap
ssh root@<host> 'mkdir -p /var/lib/dotsmoke && chown 1654:1654 /var/lib/dotsmoke'
```

`bootstrap` installs Docker. The second line creates the volume that holds `app.db`
and the `keys/` ring, owned by the image's non-root user — the container cannot
write a freshly mounted directory otherwise.

Neither file is encrypted at rest, and one of them is a log of who read which chart.
Against the public launcher that is synthetic data; point `Smart:TrustedIssuers` at a
real EHR and `/var/lib/dotsmoke/app.db` is PHI on a droplet with no disk encryption.

Then the parts a firewall would usually cover. kamal-proxy renews its certificate
itself, so 80 and 443 have to stay open to the world, which leaves the SSH port as
the thing to close down rather than off:

```bash
ssh root@<host> 'sshd -T | grep -E "^(passwordauthentication|permitrootlogin)"'
ssh root@<host> 'cat ~/.ssh/authorized_keys'   # the deploy key, and what else?
ssh root@<host> 'docker ps --format "{{.Names}}\t{{.Ports}}"'
```

Wanted: `passwordauthentication no`, `permitrootlogin prohibit-password`, nothing in
`authorized_keys` that is not accounted for, and nothing publishing a port but the
proxy. A DigitalOcean Cloud Firewall allowing 22, 80 and 443 and refusing the rest
does not get in ACME's way, and is worth having over none.

The `production` environment is the last piece, and it is a repository setting rather
than a command: it holds `DOTSMOKE_DEPLOY`, and its deployment branch policy names
`main` and nothing else. See [Secrets](#secrets) for what that buys.

Kamal waits for the new container's health check before stopping the old one, so
every deploy has a few seconds where two processes have the SQLite file open. This
is accepted rather than solved: one droplet, one file, migrations that add.

## Secrets

Two, and neither belongs to the app: an SSH key made for this droplet and nothing
else, and the `GITHUB_TOKEN` minted for the workflow run, which expires with it. So
the server holds no standing credential, and nothing the app itself holds is secret
— which is why `config/deploy.yml` carries its settings in the clear. Kamal logs the
droplet in to ghcr.io with that token before pulling, which leaves it in root's
`~/.docker/config.json` there; it is expired within hours of the run that minted it.

The key is an *environment* secret, on `production`, rather than a repository one.
That is not a formality. A repository secret is readable by any workflow run, and a
run on a pull request from a branch of this repository is one of those: it is handed
the secrets, and it runs the workflow file from that branch. So with the key at the
repository, nothing between a push and root on the droplet — no merge, no review, no
green check. An environment secret is readable only by a job that names its
environment, and this environment's deployment branch policy is `main` alone, so a
job naming it from anywhere else is refused before its first step.

What that key can do is unchanged: Kamal reaches a server over SSH and runs `docker`
there, and a user in the `docker` group is root under another name. The environment
bounds who can reach the key, not what it is. Keeping no key in GitHub at all is the
other end of the trade, and deploying by hand above is how.
